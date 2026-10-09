using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;

namespace AuthHub.Application.Interfaces;

/// <summary>
/// 账号相关业务流程：注册、登录（含 MFA 两阶段）、改密、档案查询。
/// 实现位于 Application 层（依赖 Identity 抽象，不依赖 HTTP）。
/// </summary>
public interface IAccountService
{
    Task<Result<UserProfileDto>> RegisterAsync(
        RegisterRequest request,
        string? emailConfirmationLinkBase = null,
        CancellationToken cancellationToken = default);

    /// <summary>第一阶段：校验密码。可能返回"需要 MFA"或"已锁定"。</summary>
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>第二阶段：校验 TOTP / 恢复码，成功后写入登录会话。</summary>
    Task<Result<LoginResponse>> CompleteTwoFactorAsync(
        string twoFactorToken,
        string code,
        bool rememberMachine,
        CancellationToken cancellationToken = default);

    /// <summary>登出（清除 Identity 会话 Cookie）。</summary>
    Task<Result> SignOutAsync(CancellationToken cancellationToken = default);

    Task<Result<UserProfileDto>> GetProfileAsync(CancellationToken cancellationToken = default);

    Task<Result> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 申请密码重置：按邮箱发重置链接。无论邮箱是否存在都返回成功，
    /// 避免通过响应差异枚举系统账号（用户名枚举攻击）。
    /// </summary>
    Task<Result> RequestPasswordResetAsync(string email, string? resetLinkBase, CancellationToken cancellationToken = default);

    /// <summary>用邮箱里的重置令牌改密，成功后使所有旧会话失效。</summary>
    Task<Result> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>消费邮箱确认令牌。已确认的邮箱重复调用幂等成功。</summary>
    Task<Result> ConfirmEmailAsync(string userId, string token, CancellationToken cancellationToken = default);

    /// <summary>重发邮箱确认邮件。邮箱不存在或已确认时静默成功。</summary>
    Task<Result> ResendEmailConfirmationAsync(string email, string? confirmationLinkBase, CancellationToken cancellationToken = default);

    /// <summary>生成 TOTP 密钥与恢复码（此时尚未启用）。</summary>
    Task<Result<TwoFactorSetupResponse>> BeginTwoFactorSetupAsync(CancellationToken cancellationToken = default);

    /// <summary>用 TOTP 验证码确认启用 MFA，返回恢复码。</summary>
    Task<Result<IReadOnlyCollection<string>>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default);

    Task<Result> DisableTwoFactorAsync(CancellationToken cancellationToken = default);

    /// <summary>下发一次性验证码（Email / Phone 通道）。</summary>
    Task<Result> SendTwoFactorCodeAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>列出该用户已开启的 MFA 通道。</summary>
    Task<Result<IReadOnlyCollection<string>>> GetTwoFactorProvidersAsync(CancellationToken cancellationToken = default);
}
