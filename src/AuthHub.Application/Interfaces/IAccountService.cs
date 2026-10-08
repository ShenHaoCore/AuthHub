using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using Microsoft.AspNetCore.Identity;

namespace AuthHub.Application.Interfaces;

/// <summary>
/// 账号相关业务流程：注册、登录（含 MFA 两阶段）、改密、档案查询。
/// 实现位于 Application 层（依赖 Identity 抽象，不依赖 HTTP）。
/// </summary>
public interface IAccountService
{
    Task<Result<UserProfileDto>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    /// <summary>第一阶段：校验密码。可能返回“需要 MFA”或“已锁定”。</summary>
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

    /// <summary>生成 TOTP 密钥与恢复码（此时尚未启用）。</summary>
    Task<Result<TwoFactorSetupResponse>> BeginTwoFactorSetupAsync(CancellationToken cancellationToken = default);

    /// <summary>用 TOTP 验证码确认启用 MFA，返回恢复码。</summary>
    Task<Result<IReadOnlyCollection<string>>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default);

    Task<Result> DisableTwoFactorAsync(CancellationToken cancellationToken = default);

    /// <summary>下发一次性验证码（Email / Phone 通道）。</summary>
    Task<Result> SendTwoFactorCodeAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>列出该用户已开启的 MFA 通道。</summary>
    Task<Result<IReadOnlyCollection<string>>> GetTwoFactorProvidersAsync(CancellationToken cancellationToken = default);

    // ---- 第三方登录（GitHub / Google）----

    /// <summary>
    /// 处理外部登录回调：
    /// 已有绑定 → 直接登录（跳过本地 MFA，外部 IdP 已完成身份验证）；
    /// 邮箱匹配到本地账号 → 返回 <see cref="ExternalLoginStatus.BindingConfirmationRequired"/>（不改数据）；
    /// 无本地账号且邮箱可信 → 自动建号并登录。
    /// </summary>
    Task<Result<ExternalLoginResolution>> SignInWithExternalLoginAsync(ExternalLoginInfo info, CancellationToken cancellationToken = default);

    /// <summary>绑定确认页的展示数据（只读，不做任何变更）。</summary>
    Task<Result<ExternalBindingView>> GetExternalBindingViewAsync(ExternalLoginInfo info, CancellationToken cancellationToken = default);

    /// <summary>
    /// 确认绑定：把外部登录关联到（按已验证邮箱重新定位的）本地账号并登录。
    /// 刻意不信任表单/页面传来的账号标识 —— 一切以外部 Cookie 里的身份 + 服务端查询为准。
    /// </summary>
    Task<Result<ExternalLoginResolution>> ConfirmExternalBindingAsync(ExternalLoginInfo info, CancellationToken cancellationToken = default);
}
