using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore.Identity;

namespace AuthHub.Application.Services;

/// <summary>
/// 注册、登录（两阶段：密码 → MFA）与登出（AccountService 的 partial 部分）。
/// </summary>
public partial class AccountService
{
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
            CreatedAt = _clock.UtcNow
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

        user.LastLoginAt = _clock.UtcNow;
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
}
