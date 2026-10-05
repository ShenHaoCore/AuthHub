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
    private readonly ISmsSender _smsSender;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAuditLogService audit,
        ITwoFactorTicketProtector tickets,
        IEmailSender emailSender,
        ISmsSender smsSender,
        ICurrentUser currentUser,
        ILogger<AccountService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _audit = audit;
        _tickets = tickets;
        _emailSender = emailSender;
        _smsSender = smsSender;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ------------------------------------------------------------------ 注册

    public async Task<Result<UserProfileDto>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
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

        // 发送邮箱确认邮件（默认实现只写日志，接入真实邮件服务后即可生效）
        await TrySendEmailConfirmationAsync(user, cancellationToken);

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

        var code = await _userManager.GenerateTwoFactorTokenAsync(user, provider);

        switch (provider)
        {
            case "Email" when !string.IsNullOrWhiteSpace(user.Email):
                await _emailSender.SendAsync(
                    user.Email,
                    "【AuthHub】登录验证码",
                    $"您的验证码是 {code}，5 分钟内有效。若非本人操作请立即修改密码。",
                    cancellationToken);
                return Result.Success();

            case "Phone" when !string.IsNullOrWhiteSpace(user.PhoneNumber):
                await _smsSender.SendAsync(
                    user.PhoneNumber,
                    $"【AuthHub】验证码 {code}，5 分钟内有效。",
                    cancellationToken);
                return Result.Success();

            default:
                return Result.Failure(Error.Validation($"当前账号未配置 {provider} 通道所需的联系方式。"));
        }
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
        var permissions = RolePermissionMap.ResolvePermissions(roles);

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

    private async Task TrySendEmailConfirmationAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.Email)) return;

        try
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            await _emailSender.SendAsync(
                user.Email,
                "【AuthHub】请确认您的邮箱",
                $"请调用 POST /api/account/email/confirm 完成确认。\nuserId: {user.Id}\ntoken: {token}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            // 邮件通道不可用不能导致注册失败
            _logger.LogWarning(ex, "发送邮箱确认邮件失败，用户 {UserId}", user.Id);
        }
    }
}
