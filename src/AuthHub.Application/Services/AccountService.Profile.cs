using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace AuthHub.Application.Services;

/// <summary>
/// 档案查询、密码（改密 / 忘记与重置）与邮箱确认（AccountService 的 partial 部分）。
/// </summary>
public partial class AccountService
{
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

    // ------------------------------------------------------------------ 忘记密码 / 重置密码

    /// <summary>申请密码重置：按邮箱查找用户，生成 Identity 签名的重置令牌并发送重置链接。无论邮箱是否存在都返回成功，避免通过响应差异枚举邮箱；真实发信只在用户存在时进行。</summary>
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

    /// <summary>用重置令牌改密。令牌由 Identity 产出并用 DataProtection 签名（本项目有效期 1 小时）；成功后安全戳更新，其他设备上的旧 Cookie 会话失效。</summary>
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
            // 密码策略失败透出真实原因方便改密重试；令牌无效/过期统一模糊提示，避免泄露校验细节
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

    /// <summary>重发邮箱确认邮件。已确认的邮箱不再重发；不区分邮箱是否存在，统一返回成功。</summary>
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
}
