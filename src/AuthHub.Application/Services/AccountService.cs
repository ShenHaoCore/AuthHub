using System.Globalization;
using System.Security.Claims;
using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly ExternalLoginOptions _externalLogin;
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
        IOptions<ExternalLoginOptions> externalLoginOptions,
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
        _externalLogin = externalLoginOptions.Value;
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

    // ------------------------------------------------------------------ 第三方登录（GitHub / Google）

    public async Task<Result<ExternalLoginResolution>> SignInWithExternalLoginAsync(
        ExternalLoginInfo info,
        CancellationToken cancellationToken = default)
    {
        var provider = info.LoginProvider;
        var hasTrustedEmail = TryResolveTrustedEmail(info, out var email, out var externalName);

        // ⓪ 邮箱白名单（典型用途：开发环境只放自己的测试账号）。闸门放在一切账号查找之前，
        // 已绑定账号也不例外 —— 否则在名单放开期绑定一次，收紧后仍可登录，白名单就形同虚设。
        // 名单非空却拿不到可信邮箱时按未命中处理（fail-closed），绝不放行。
        if (!IsExternalEmailAllowed(hasTrustedEmail ? email : null))
        {
            var attempted = string.IsNullOrWhiteSpace(externalName) ? provider : externalName;
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserExternalLoginFailed, false, UserName: attempted,
                    Details: $"{provider} 登录：邮箱不在允许名单内"),
                cancellationToken);
            return Result.Failure<ExternalLoginResolution>(Error.Forbidden(
                "该第三方账号不在允许登录的名单内。如需访问，请联系管理员将你的邮箱加入白名单。"));
        }

        // ① 已建立过绑定：直接登录
        var linked = await _userManager.FindByLoginAsync(provider, info.ProviderKey!);
        if (linked is not null)
        {
            if (!linked.IsActive)
            {
                await _audit.LogAsync(
                    new AuditEntry(AuditActionType.UserExternalLoginFailed, false, linked.Id, linked.UserName, Details: $"{provider} 登录：账号已停用"),
                    cancellationToken);
                return Result.Failure<ExternalLoginResolution>(Error.Forbidden("账号已被停用，请联系管理员。"));
            }

            // 有意的决策：外部登录跳过本地 MFA 第二因子 —— 外部 IdP 已完成身份验证
            //（通常含其自身的两步验证），本地 2FA 保护的只是「密码」这条登录通道。
            await SignInAndStampAsync(linked, isPersistent: false, provider, cancellationToken);
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserExternalLoginSucceeded, true, linked.Id, linked.UserName, Details: $"{provider} 登录"),
                cancellationToken);
            return Result.Success(new ExternalLoginResolution(
                ExternalLoginStatus.SignedIn, linked.Id, linked.UserName!, linked.DisplayName, IsNewAccount: false));
        }

        // ② 未绑定：账号匹配与建号都以「提供商已验证的邮箱」为锚点，拿不到就明确拒绝。
        // 不信未验证邮箱 —— 否则攻击者用可控的未验证邮箱即可冒名匹配本地账号。
        //（可信邮箱在方法开头已解析一次，白名单闸门外的所有分支共用同一结果。）
        if (!hasTrustedEmail)
        {
            var attempted = string.IsNullOrWhiteSpace(externalName) ? provider : externalName;
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserExternalLoginFailed, false, UserName: attempted, Details: $"{provider} 登录：外部邮箱缺失或未验证"),
                cancellationToken);
            return Result.Failure<ExternalLoginResolution>(Error.Unauthorized(
                "无法从该外部账号获取已验证的邮箱。请先在提供商处完成邮箱验证，或使用账号密码登录。"));
        }

        var matched = await _userManager.FindByEmailAsync(email);
        if (matched is not null)
        {
            if (!matched.IsActive)
            {
                await _audit.LogAsync(
                    new AuditEntry(AuditActionType.UserExternalLoginFailed, false, matched.Id, matched.UserName, Details: $"{provider} 登录：账号已停用"),
                    cancellationToken);
                return Result.Failure<ExternalLoginResolution>(Error.Forbidden("账号已被停用，请联系管理员。"));
            }

            // 匹配到本地账号：不自动绑定（自动绑定等于承认"控制邮箱即可控制账号"），
            // 交给用户在确认页显式确认 —— 提供商的已验证邮箱只证明"此刻控制该邮箱"。
            return Result.Success(new ExternalLoginResolution(
                ExternalLoginStatus.BindingConfirmationRequired, matched.Id, matched.UserName!, matched.DisplayName, IsNewAccount: false));
        }

        // ③ 无本地账号：自动建号。邮箱已经提供商验证，直接置为已确认（邮箱通道的 MFA 因此可用）。
        var userName = await BuildUniqueUserNameAsync(info, email);
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            DisplayName = ResolveDisplayName(info.Principal, userName),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var create = await _userManager.CreateAsync(user);
        if (!create.Succeeded)
        {
            return Result.Failure<ExternalLoginResolution>(create.ToError());
        }

        // 新用户默认进入 User 角色（无管理权限），与密码注册的口径一致
        await _userManager.AddToRoleAsync(user, AuthHubConstants.Roles.User);

        var link = await _userManager.AddLoginAsync(user, new UserLoginInfo(provider, info.ProviderKey!, provider));
        if (!link.Succeeded)
        {
            return Result.Failure<ExternalLoginResolution>(link.ToError());
        }

        await SignInAndStampAsync(user, isPersistent: false, provider, cancellationToken);
        await _audit.LogAsync(
            new AuditEntry(AuditActionType.UserExternalLoginSucceeded, true, user.Id, user.UserName, Details: $"{provider} 首次登录，自动创建账号"),
            cancellationToken);

        return Result.Success(new ExternalLoginResolution(
            ExternalLoginStatus.SignedIn, user.Id, user.UserName!, user.DisplayName, IsNewAccount: true));
    }

    public async Task<Result<ExternalBindingView>> GetExternalBindingViewAsync(
        ExternalLoginInfo info,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTrustedEmail(info, out var email, out var externalName))
        {
            return Result.Failure<ExternalBindingView>(Error.Unauthorized("无法从该外部账号获取已验证的邮箱，无法建立绑定。"));
        }

        // 白名单与登录入口同口径：登录被挡的账号也不该拿到绑定确认页
        if (!IsExternalEmailAllowed(email))
        {
            return Result.Failure<ExternalBindingView>(Error.Forbidden(
                "该第三方账号不在允许登录的名单内，无法建立绑定。"));
        }

        var local = await _userManager.FindByEmailAsync(email);
        if (local is null || !local.IsActive)
        {
            return Result.Failure<ExternalBindingView>(Error.NotFound("没有匹配的本地账号，或账号已停用。"));
        }

        return Result.Success(new ExternalBindingView(
            info.LoginProvider,
            email,
            externalName,
            local.Id,
            local.UserName ?? local.Id,
            local.Email ?? email));
    }

    public async Task<Result<ExternalLoginResolution>> ConfirmExternalBindingAsync(
        ExternalLoginInfo info,
        CancellationToken cancellationToken = default)
    {
        var provider = info.LoginProvider;

        if (!TryResolveTrustedEmail(info, out var email, out _))
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserExternalLoginFailed, false, UserName: provider, Details: $"{provider} 绑定确认：外部邮箱缺失或未验证"),
                cancellationToken);
            return Result.Failure<ExternalLoginResolution>(Error.Unauthorized("无法从该外部账号获取已验证的邮箱，无法建立绑定。"));
        }

        // 即使外部 Cookie 是白名单收紧前留下的，确认绑定这一步仍然按当前名单再挡一次
        if (!IsExternalEmailAllowed(email))
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserExternalLoginFailed, false, UserName: provider, Details: $"{provider} 绑定确认：邮箱不在允许名单内"),
                cancellationToken);
            return Result.Failure<ExternalLoginResolution>(Error.Forbidden(
                "该第三方账号不在允许登录的名单内，无法建立绑定。"));
        }

        // 重新按邮箱定位本地账号 —— 表单里不带任何身份字段，绑定向导无法被表单参数操纵
        var user = await _userManager.FindByEmailAsync(email);
        if (user is null || !user.IsActive)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserExternalLoginFailed, false, user?.Id, user?.UserName, Details: $"{provider} 绑定确认：本地账号不存在或已停用"),
                cancellationToken);
            return Result.Failure<ExternalLoginResolution>(Error.Unauthorized("本地账号不存在或已停用，无法绑定。"));
        }

        // 绑定键 (LoginProvider, ProviderKey) 在库上有唯一索引：确认页停留期间被另一会话抢先绑定时，
        // 显式报冲突而不是让数据库异常裸奔
        var existing = await _userManager.FindByLoginAsync(provider, info.ProviderKey!);
        if (existing is not null && existing.Id != user.Id)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.UserExternalLoginFailed, false, user.Id, user.UserName, Details: $"{provider} 绑定确认：该外部账号已绑定其他本地账号"),
                cancellationToken);
            return Result.Failure<ExternalLoginResolution>(Error.Conflict("该外部账号已绑定其他 AuthHub 账号。"));
        }

        if (existing is null)
        {
            var addResult = await _userManager.AddLoginAsync(user, new UserLoginInfo(provider, info.ProviderKey!, provider));
            if (!addResult.Succeeded)
            {
                return Result.Failure<ExternalLoginResolution>(addResult.ToError());
            }
        }

        await SignInAndStampAsync(user, isPersistent: false, provider, cancellationToken);
        await _audit.LogAsync(
            new AuditEntry(AuditActionType.UserExternalLoginSucceeded, true, user.Id, user.UserName, Details: $"{provider} 登录（绑定已有账号）"),
            cancellationToken);

        return Result.Success(new ExternalLoginResolution(
            ExternalLoginStatus.SignedIn, user.Id, user.UserName!, user.DisplayName, IsNewAccount: false));
    }

    // ------------------------------------------------------------------ 内部辅助

    private async Task<ApplicationUser?> FindByIdentifierAsync(string identifier)
        => identifier.Contains('@', StringComparison.Ordinal)
            ? await _userManager.FindByEmailAsync(identifier)
            : await _userManager.FindByNameAsync(identifier);

    /// <summary>
    /// 从外部身份解析「已验证」的邮箱。
    ///
    /// 信任判据只认显式的 <c>email_verified=true</c> 声明（两个提供商的处理器都由
    /// OnCreatingTicket 事件补写，见 ExternalAuthenticationExtensions），不按提供商名
    /// 做任何"应该可信"的推断 —— 判据错了就是账号接管，宁可误拒。
    /// </summary>
    private static bool TryResolveTrustedEmail(ExternalLoginInfo info, out string email, out string externalName)
    {
        email = string.Empty;
        externalName = info.Principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

        var candidate = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(candidate) || !IsEmailVerified(info))
        {
            return false;
        }

        email = candidate;
        return true;
    }

    private static bool IsEmailVerified(ExternalLoginInfo info)
        => info.Principal.Claims.Any(claim =>
            claim.Type.EndsWith("email_verified", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(claim.Value, out var verified) && verified);

    /// <summary>
    /// 外部邮箱是否在允许名单内：名单为空表示不限制（生产默认）；非空时按忽略大小写精确匹配，
    /// 配置项里的空白条目会被跳过。<paramref name="email"/> 为 null（拿不到可信邮箱）时
    /// 在非空名单下返回 false —— 与调用方约定的 fail-closed 口径一致。
    /// </summary>
    private bool IsExternalEmailAllowed(string? email)
    {
        var allowed = _externalLogin.AllowedEmails;
        if (allowed is null || allowed.Length == 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(email) &&
               allowed.Any(item =>
                   !string.IsNullOrWhiteSpace(item) &&
                   string.Equals(item.Trim(), email, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>为新外部用户起一个不冲突的用户名：优先用提供商给的标识，其次邮箱前缀。</summary>
    private async Task<string> BuildUniqueUserNameAsync(ExternalLoginInfo info, string email)
    {
        var preferred = info.Principal.FindFirstValue(ClaimTypes.Name)
                        ?? email[..email.IndexOf('@', StringComparison.Ordinal)];

        // Identity 默认的用户名字符集（字母数字与 -._@+），超出的剥掉
        const string Allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
        var baseName = new string(preferred.Where(Allowed.Contains).ToArray());

        if (baseName.Length < 3)
        {
            baseName = "user";
        }

        var candidate = baseName;
        for (var i = 1; await _userManager.FindByNameAsync(candidate) is not null; i++)
        {
            candidate = $"{baseName}{i}";
        }

        return candidate;
    }

    private static string ResolveDisplayName(ClaimsPrincipal principal, string fallbackUserName)
        => principal.FindFirstValue(ClaimTypes.Name) ?? fallbackUserName;

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
