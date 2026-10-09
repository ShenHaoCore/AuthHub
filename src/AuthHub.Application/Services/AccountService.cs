using System.Globalization;
using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace AuthHub.Application.Services;

/// <summary>
/// 账号业务：注册、两阶段登录（密码 + MFA）、改密、MFA 生命周期管理。
/// 只依赖 Identity 的抽象（UserManager / SignInManager），不依赖 HTTP，
/// 因此可以被单元测试直接驱动。
/// </summary>
public sealed class AccountService : IAccountService
{
    private const string AuthenticatorIssuer = "AuthHub";
    private const int RecoveryCodeCount = 10;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IAuditLogService _audit;
    private readonly ITwoFactorTicketProtector _tickets;
    private readonly IEmailSender _emailSender;
    private readonly IReadOnlyDictionary<string, ITwoFactorChannel> _twoFactorChannels;
    private readonly ICurrentUser _currentUser;
    private readonly IRolePermissionMap _rolePermissions;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAuditLogService audit,
        ITwoFactorTicketProtector tickets,
        IEmailSender emailSender,
        IEnumerable<ITwoFactorChannel> twoFactorChannels,
        ICurrentUser currentUser,
        IRolePermissionMap rolePermissions,
        ILogger<AccountService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _audit = audit;
        _tickets = tickets;
        _emailSender = emailSender;
        // 按通道名索引。重名直接炸在启动期 —— 否则运行期会静默用后注册的那个覆盖先注册的，
        // 表现为"某个通道的验证码发到了另一个通道的文案里"，很难查。
        _twoFactorChannels = twoFactorChannels.ToDictionary(
            channel => channel.Provider,
            StringComparer.Ordinal);
        _currentUser = currentUser;
        _rolePermissions = rolePermissions;
        _logger = logger;
    }

    // ------------------------------------------------------------------ 注册

    public async Task<Result<UserProfileDto>> RegisterAsync(
        RegisterRequest request,
        string? emailConfirmationLinkBase = null,
        CancellationToken cancellationToken = default)
    {
        var user = new ApplicationUser
        {
            UserName = request.UserName.Trim(),
            Email = request.Email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.UserName.Trim() : request.DisplayName.Trim(),
            // 注册后邮箱处于未确认状态：Email/SMS 双因子通道依赖已确认的邮箱/手机号。
            // 开发环境不会因未确认邮箱而拒绝登录（IdentityOptions.SignIn.RequireConfirmedEmail = false）。
            EmailConfirmed = false,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            return Result.Failure<UserProfileDto>(createResult.ToError());
        }

        // 新用户默认进入 User 角色（无管理权限）
        await _userManager.AddToRoleAsync(user, AuthHubConstants.Roles.User);

        await _audit.LogAsync(new AuditEntry(AuditActionType.UserRegistered, true, user.Id, user.UserName), cancellationToken);

        // 发送邮箱确认邮件（默认实现只写日志，接入真实邮件服务后即可生效）。
        // 链接基址由控制器按当前请求的对外地址拼装（ForwardedHeaders 还原公网入口）。
        await TrySendEmailConfirmationAsync(user, emailConfirmationLinkBase, cancellationToken);

        return await BuildProfileAsync(user, cancellationToken);
    }

    // ------------------------------------------------------------------ 登录（第一阶段）

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var identifier = request.UserNameOrEmail.Trim();
        var user = await FindByIdentifierAsync(identifier);

        // 用户不存在时也走一次密码哈希校验，避免通过响应时间枚举用户名
        if (user is null)
        {
            await _signInManager.CheckPasswordSignInAsync(
                new ApplicationUser { UserName = identifier },
                request.Password,
                lockoutOnFailure: false);

            await _audit.LogLoginFailedAsync(null, identifier, "用户不存在", cancellationToken);
            return Result.Failure<LoginResponse>(Error.Unauthorized("用户名或密码错误。"));
        }

        if (!user.IsActive)
        {
            await _audit.LogLoginFailedAsync(user.Id, user.UserName!, "账号已停用", cancellationToken);
            return Result.Failure<LoginResponse>(Error.Forbidden("账号已被停用，请联系管理员。"));
        }

        var signInResult = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (signInResult.IsLockedOut)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserLockedOut, false, user.Id, user.UserName, Details: "连续登录失败触发锁定"),
                cancellationToken);

            return Result.Failure<LoginResponse>(Error.LockedOut("登录失败次数过多，账号已被临时锁定，请稍后再试。"));
        }

        if (signInResult.RequiresTwoFactor)
        {
            // 密码正确但还需第二因子：此时绝不建立正式会话
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserLoginSucceeded, true, user.Id, user.UserName, Details: "密码校验通过，等待 MFA"),
                cancellationToken);

            var ticket = _tickets.Protect(user.Id, request.RememberMe);
            return Result.Success(LoginResponse.TwoFactorRequired(user.Id, user.UserName!, ticket));
        }

        if (!signInResult.Succeeded)
        {
            await _audit.LogLoginFailedAsync(user.Id, user.UserName!, "密码错误", cancellationToken);
            return Result.Failure<LoginResponse>(Error.Unauthorized("用户名或密码错误。"));
        }

        await SignInAndStampAsync(user, request.RememberMe, "Password", cancellationToken);
        await _audit.LogLoginSucceededAsync(user.Id, user.UserName!, cancellationToken: cancellationToken);

        return Result.Success(LoginResponse.Success(user.Id, user.UserName!, user.DisplayName));
    }

    // ------------------------------------------------------------------ 登录（第二阶段：MFA）

    public async Task<Result<LoginResponse>> CompleteTwoFactorAsync(
        string twoFactorToken,
        string code,
        bool rememberMachine,
        CancellationToken cancellationToken = default)
    {
        var ticket = _tickets.Unprotect(twoFactorToken);
        if (ticket is null)
        {
            return Result.Failure<LoginResponse>(Error.Unauthorized("两阶段验证凭据无效或已过期，请重新登录。"));
        }

        var user = await _userManager.FindByIdAsync(ticket.UserId);
        if (user is null || !user.IsActive)
        {
            return Result.Failure<LoginResponse>(Error.Unauthorized("账号不存在或已被停用。"));
        }

        var normalizedCode = code.Trim().Replace(" ", string.Empty);

        // 先按 Authenticator（TOTP）验证，失败再按恢复码验证。
        // TwoFactorAuthenticatorSignInAsync 内部会执行 PreSignInCheck（邮箱/手机号确认状态等）。
        var result = await _signInManager.TwoFactorAuthenticatorSignInAsync(
            normalizedCode, ticket.RememberMe, rememberMachine);

        var usedRecoveryCode = false;
        if (!result.Succeeded && !result.IsLockedOut && !result.IsNotAllowed)
        {
            var recoveryResult = await _signInManager.TwoFactorRecoveryCodeSignInAsync(normalizedCode);
            if (recoveryResult.Succeeded)
            {
                result = recoveryResult;
                usedRecoveryCode = true;
            }
        }

        if (result.IsLockedOut)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserLockedOut, false, user.Id, user.UserName, Details: "MFA 连续失败触发锁定"),
                cancellationToken);

            return Result.Failure<LoginResponse>(Error.LockedOut("验证失败次数过多，账号已被临时锁定。"));
        }

        if (result.IsNotAllowed)
        {
            await _audit.LogLoginFailedAsync(user.Id, user.UserName ?? user.Id, "账号不满足登录前置条件", cancellationToken);
            return Result.Failure<LoginResponse>(Error.Forbidden("账号当前不允许登录（例如邮箱或手机号尚未确认）。"));
        }

        if (!result.Succeeded)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.TwoFactorFailed, false, user.Id, user.UserName, Details: "验证码错误"),
                cancellationToken);

            return Result.Failure<LoginResponse>(Error.Unauthorized("验证码不正确或已过期。"));
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        user.LastLoginIp = _currentUser.IpAddress;
        await _userManager.UpdateAsync(user);

        await _audit.LogAsync(
            new AuditEntry(
                usedRecoveryCode ? AuditActionType.TwoFactorRecoveryCodeUsed : AuditActionType.UserLoginSucceeded,
                true,
                user.Id,
                user.UserName,
                Details: usedRecoveryCode ? "使用恢复码完成 MFA" : "MFA 校验通过"),
            cancellationToken);

        return Result.Success(LoginResponse.Success(user.Id, user.UserName!, user.DisplayName));
    }

    public async Task<Result> SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (_currentUser.IsAuthenticated && _currentUser.UserId is { } userId)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserLogout, true, userId, _currentUser.UserName),
                cancellationToken);
        }

        await _signInManager.SignOutAsync();
        return Result.Success();
    }

    // ------------------------------------------------------------------ 档案 / 改密

    public async Task<Result<UserProfileDto>> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure<UserProfileDto>(Error.Unauthorized("未登录。"));
        }

        return await BuildProfileAsync(user, cancellationToken);
    }

    public async Task<Result> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure(Error.Unauthorized("未登录。"));
        }

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return Result.Failure(result.ToError());
        }

        // 刷新会话 Cookie 中的安全戳，使其他设备上的旧会话失效
        await _signInManager.RefreshSignInAsync(user);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.UserPasswordChanged, true, user.Id, user.UserName),
            cancellationToken);

        return Result.Success();
    }

    // ------------------------------------------------------------------ MFA 生命周期

    public async Task<Result<TwoFactorSetupResponse>> BeginTwoFactorSetupAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure<TwoFactorSetupResponse>(Error.Unauthorized("未登录。"));
        }

        var key = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            key = await _userManager.GetAuthenticatorKeyAsync(user);
        }

        if (string.IsNullOrEmpty(key))
        {
            return Result.Failure<TwoFactorSetupResponse>(Error.Failure("TwoFactorKey", "无法生成 Authenticator 密钥。"));
        }

        var accountName = user.Email ?? user.UserName ?? user.Id;
        var uri = string.Format(
            CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6&period=30",
            Uri.EscapeDataString(AuthenticatorIssuer),
            Uri.EscapeDataString(accountName),
            key);

        // 恢复码在这里一并生成：用户确认 6 位码之前不会被启用
        var recoveryCodes = await GenerateRecoveryCodesInternalAsync(user);

        return Result.Success(new TwoFactorSetupResponse(key, uri, recoveryCodes));
    }

    public async Task<Result<IReadOnlyCollection<string>>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure<IReadOnlyCollection<string>>(Error.Unauthorized("未登录。"));
        }

        var isValid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            TokenOptions.DefaultAuthenticatorProvider,
            code.Replace(" ", string.Empty));

        if (!isValid)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.TwoFactorFailed, false, user.Id, user.UserName, Details: "启用 MFA 时验证码校验失败"),
                cancellationToken);

            return Result.Failure<IReadOnlyCollection<string>>(Error.Unauthorized("验证码不正确，请确认 App 时间同步后重试。"));
        }

        await _userManager.SetTwoFactorEnabledAsync(user, true);

        // 启用时重置恢复码，确保返回给用户的就是当前有效的那一批
        var recoveryCodes = await GenerateRecoveryCodesInternalAsync(user);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.TwoFactorEnabled, true, user.Id, user.UserName),
            cancellationToken);

        return Result.Success<IReadOnlyCollection<string>>(recoveryCodes);
    }

    public async Task<Result> DisableTwoFactorAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure(Error.Unauthorized("未登录。"));
        }

        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.TwoFactorDisabled, true, user.Id, user.UserName),
            cancellationToken);

        return Result.Success();
    }

    public async Task<Result> SendTwoFactorCodeAsync(string provider, CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure(Error.Unauthorized("未登录。"));
        }

        // 顺序刻意与改造前一致：**先**生成验证码，**再**找通道。
        // 反过来写会在 provider 非法时改变 UserManager 的调用时机 —— 那属于未经请求的行为变更。
        var code = await _userManager.GenerateTwoFactorTokenAsync(user, provider);

        if (!_twoFactorChannels.TryGetValue(provider, out var channel) ||
            channel.ResolveTarget(user) is not { } target)
        {
            // 「通道不存在」与「通道存在但用户没留联系方式」共用同一条消息，是既有响应契约。
            // 想分开写得更准，要先确认调用方/文档没有依赖这句原文。
            return Result.Failure(Error.Validation($"当前账号未配置 {provider} 通道所需的联系方式。"));
        }

        await channel.SendAsync(target, code, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyCollection<string>>> GetTwoFactorProvidersAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure<IReadOnlyCollection<string>>(Error.Unauthorized("未登录。"));
        }

        var providers = await _userManager.GetValidTwoFactorProvidersAsync(user);
        return Result.Success<IReadOnlyCollection<string>>(providers.ToArray());
    }

    // ------------------------------------------------------------------ 内部辅助

    private async Task<ApplicationUser?> FindByIdentifierAsync(string identifier)
        => identifier.Contains('@', StringComparison.Ordinal)
            ? await _userManager.FindByEmailAsync(identifier)
            : await _userManager.FindByNameAsync(identifier);

    private async Task<ApplicationUser?> GetCurrentUserAsync()
        => _currentUser.UserId is { } userId ? await _userManager.FindByIdAsync(userId) : null;

    private async Task SignInAndStampAsync(
        ApplicationUser user,
        bool isPersistent,
        string authenticationMethod,
        CancellationToken cancellationToken)
    {
        await _signInManager.SignInAsync(user, isPersistent, authenticationMethod);

        user.LastLoginAt = DateTimeOffset.UtcNow;
        user.LastLoginIp = _currentUser.IpAddress;

        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            // 只影响审计字段，不阻断登录
            _logger.LogWarning("更新用户最后登录信息失败：{Errors}", string.Join("; ", update.Errors.Select(e => e.Description)));
        }
    }

    private async Task<Result<UserProfileDto>> BuildProfileAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var permissions = _rolePermissions.ResolvePermissions(roles);

        // 权限声明在令牌/会话里也需要，因此额外写回身份
        var profile = new UserProfileDto
        {
            Id = user.Id,
            UserName = user.UserName ?? string.Empty,
            Email = user.Email,
            DisplayName = user.DisplayName,
            PhoneNumber = user.PhoneNumber,
            EmailConfirmed = user.EmailConfirmed,
            TwoFactorEnabled = user.TwoFactorEnabled,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            Roles = roles.ToArray(),
            Permissions = permissions
        };

        return Result.Success(profile);
    }

    private async Task<IReadOnlyCollection<string>> GenerateRecoveryCodesInternalAsync(ApplicationUser user)
    {
        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        return codes?.ToArray() ?? Array.Empty<string>();
    }

    /// <returns>是否真正发出确认邮件（失败时已记日志，不抛给调用方）。</returns>
    private async Task<bool> TrySendEmailConfirmationAsync(ApplicationUser user, string? linkBase, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.Email)) return false;

        try
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var link = BuildTokenLink(linkBase, "/account/confirm-email", ("userId", user.Id), ("token", token));
            await _emailSender.SendAsync(
                user.Email,
                "【AuthHub】请确认您的邮箱",
                $"请点击下方链接完成邮箱确认（链接 1 小时内有效）：\n\n{link}\n\n" +
                "如果不是你本人操作，请忽略此邮件。",
                cancellationToken);
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserEmailConfirmationRequested, true, user.Id, user.UserName),
                cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            // 邮件通道不可用不能导致注册失败
            _logger.LogWarning(ex, "发送邮箱确认邮件失败，用户 {UserId}", user.Id);
            return false;
        }
    }

    // ------------------------------------------------------------------ 忘记密码 / 重置密码

    /// <summary>
    /// 申请密码重置：按邮箱查找用户，生成 Identity 签名的重置令牌并通过邮件发送重置链接。
    ///
    /// <para>
    /// 无论邮箱是否存在都返回成功 —— 避免通过响应差异枚举系统里有哪些邮箱
    /// （用户名枚举攻击）。真实的发信只在用户存在时进行。
    /// </para>
    /// </summary>
    public async Task<Result> RequestPasswordResetAsync(string email, string? resetLinkBase, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim();
        var user = await _userManager.FindByEmailAsync(normalizedEmail);
        if (user is null)
        {
            // 不泄露"该邮箱是否注册"
            return Result.Success();
        }

        if (!user.IsActive)
        {
            // 账号已停用：同样不发信、不区分响应
            return Result.Success();
        }

        try
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var link = BuildTokenLink(resetLinkBase, "/account/reset-password", ("email", user.Email ?? string.Empty), ("token", token));
            await _emailSender.SendAsync(
                user.Email!,
                "【AuthHub】重置您的密码",
                $"请点击下方链接重置密码（链接 1 小时内有效）：\n\n{link}\n\n" +
                "如果不是你本人操作，请忽略此邮件，你的密码不会被修改。",
                cancellationToken);

            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserPasswordResetRequested, true, user.Id, user.UserName),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // 邮件失败不向调用方暴露，避免区分"用户存在但邮件发不出"；审计记失败便于排查
            _logger.LogWarning(ex, "发送密码重置邮件失败，用户 {UserId}", user.Id);
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserPasswordResetRequested, false, user.Id, user.UserName, Details: "邮件发送失败"),
                cancellationToken);
        }

        return Result.Success();
    }

    /// <summary>
    /// 用重置令牌改密。令牌由 <c>UserManager.GeneratePasswordResetTokenAsync</c> 产出，
    /// Identity 用 DataProtection 签名，有效期由 <c>DataProtectionTokenProviderOptions.TokenLifespan</c> 控制（本项目 1 小时）。
    /// 成功后 <c>ResetPasswordAsync</c> 会更新安全戳，使其他设备上的旧 Cookie 会话失效。
    /// </summary>
    public async Task<Result> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            // 与令牌无效同文案、同错误类型，避免通过 404/400 差异枚举邮箱
            return Result.Failure(Error.Validation("重置链接无效或已过期，请重新申请。"));
        }

        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            // 密码策略失败（强度不足等）应透出真实原因，方便用户改密重试；
            // 令牌无效/过期则统一模糊提示，避免泄露校验细节。
            if (IsPasswordPolicyFailure(result))
            {
                await _audit.LogAsync(
                    new AuditEntry(AuditActionType.UserPasswordReset, false, user.Id, user.UserName, Details: "新密码不符合策略"),
                    cancellationToken);
                return Result.Failure(result.ToError());
            }

            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserPasswordReset, false, user.Id, user.UserName, Details: "令牌无效或已过期"),
                cancellationToken);
            return Result.Failure(Error.Validation("重置链接无效或已过期，请重新申请。"));
        }

        // 安全戳已在 UserManager.ResetPasswordAsync → UpdatePasswordHash 内更新，无需再 RefreshSignIn。
        // 匿名重置场景下 RefreshSignInAsync 是 no-op 且会打 Error 日志。

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.UserPasswordReset, true, user.Id, user.UserName),
            cancellationToken);

        return Result.Success();
    }

    /// <summary>Identity 密码策略错误的 Code 均以 <c>Password</c> 开头（如 PasswordTooShort）。</summary>
    private static bool IsPasswordPolicyFailure(IdentityResult result)
        => result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ 邮箱确认

    /// <summary>消费邮箱确认令牌，把 EmailConfirmed 置为 true。</summary>
    public async Task<Result> ConfirmEmailAsync(string userId, string token, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return Result.Failure(Error.NotFound("确认链接无效。"));
        }

        if (user.EmailConfirmed)
        {
            // 幂等：已确认再点链接直接成功
            return Result.Success();
        }

        var result = await _userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserEmailConfirmed, false, user.Id, user.UserName, Details: "令牌无效或已过期"),
                cancellationToken);
            return Result.Failure(Error.Validation("确认链接无效或已过期，请重新申请。"));
        }

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.UserEmailConfirmed, true, user.Id, user.UserName),
            cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// 重发邮箱确认邮件。已确认的邮箱不再重发（避免无意义发信）。
    /// 同样不区分"邮箱不存在"，统一返回成功。
    /// </summary>
    public async Task<Result> ResendEmailConfirmationAsync(string email, string? confirmationLinkBase, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(email.Trim());
        if (user is null || user.EmailConfirmed)
        {
            return Result.Success();
        }

        var sent = await TrySendEmailConfirmationAsync(user, confirmationLinkBase, cancellationToken);
        if (sent)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserEmailConfirmationResent, true, user.Id, user.UserName),
                cancellationToken);
        }

        return Result.Success();
    }

    /// <summary>
    /// 把 token 链接拼成绝对地址。token 由 Identity 产出，可能含 + / = 等 URL 不安全字符，
    /// 必须用 <c>System.Uri.EscapeDataString</c> 编码。
    /// </summary>
    private static string BuildTokenLink(string? baseUrl, string path, params (string Key, string Value)[] query)
    {
        var schemeAndHost = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
        var queryString = string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        return $"{schemeAndHost}{path}?{queryString}";
    }
}
