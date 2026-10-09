using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Api.Extensions;
using AuthHub.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthHub.Api.Controllers;

/// <summary>
/// 账号自助 API（供 SPA / 移动端等非浏览器渲染场景调用）。
///
/// 认证方式为 Identity 会话 Cookie：调用 <c>/connect/authorize</c> 之外的
/// “登录 / 注册 / 改密 / 绑定 MFA”都发生在本服务的域内，
/// 因此使用 Cookie 而非 Bearer 令牌。
/// </summary>
[Tags("账号 API")]
[Route("api/account")]
[Authorize(AuthenticationSchemes = AuthHubSchemes.Cookie)]
[Produces("application/json")]
public class AccountApiController : ApiControllerBase
{
    private readonly IAccountService _accountService;
    private readonly IConsentService _consentService;
    private readonly ICurrentUser _currentUser;
    private readonly IConfiguration _configuration;

    public AccountApiController(
        IAccountService accountService,
        IConsentService consentService,
        ICurrentUser currentUser,
        IConfiguration configuration)
    {
        _accountService = accountService;
        _consentService = consentService;
        _currentUser = currentUser;
        _configuration = configuration;
    }

    /// <summary>注册新账号。</summary>
    [EndpointSummary("注册新账号")]
    [EndpointDescription("注册成功后需登录才能获得会话。")]
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public Task<IActionResult> Register([FromBody] RegisterRequest request)
        => ExecuteAsync(() => _accountService.RegisterAsync(request, GetBaseUrl(), HttpContext.RequestAborted));

    /// <summary>
    /// 申请密码重置：向邮箱发送重置链接。无论邮箱是否存在都返回 204，
    /// 避免通过响应差异枚举系统账号。
    /// </summary>
    [EndpointSummary("申请密码重置")]
    [EndpointDescription("向邮箱发送重置链接；无论邮箱是否存在均返回 204。")]
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        => ExecuteAsync(() => _accountService.RequestPasswordResetAsync(request.Email, GetBaseUrl(), HttpContext.RequestAborted));

    /// <summary>用邮箱里的重置令牌改密。</summary>
    [EndpointSummary("重置密码")]
    [EndpointDescription("用邮件链接里的令牌设置新密码。")]
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        => ExecuteAsync(() => _accountService.ResetPasswordAsync(request.Email, request.Token, request.NewPassword, HttpContext.RequestAborted));

    /// <summary>消费邮箱确认令牌。</summary>
    [EndpointSummary("确认邮箱")]
    [EndpointDescription("用邮件链接里的令牌把邮箱标记为已确认。")]
    [HttpGet("email/confirm")]
    [AllowAnonymous]
    public Task<IActionResult> ConfirmEmail([FromQuery] string userId, [FromQuery] string token)
        => ExecuteAsync(() => _accountService.ConfirmEmailAsync(userId, token, HttpContext.RequestAborted));

    /// <summary>重发邮箱确认邮件。</summary>
    [EndpointSummary("重发邮箱确认邮件")]
    [EndpointDescription("邮箱未确认时重发确认信；已确认或不存在时静默成功。")]
    [HttpPost("email/resend-confirmation")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public Task<IActionResult> ResendConfirmation([FromBody] ResendEmailConfirmationRequest request)
        => ExecuteAsync(() => _accountService.ResendEmailConfirmationAsync(request.Email, GetBaseUrl(), HttpContext.RequestAborted));

    /// <summary>
    /// 登录。启用 MFA 的账号会返回 <c>requiresTwoFactor=true</c> 与一次性 <c>twoFactorToken</c>，
    /// 需再调用 <c>/api/account/2fa/verify</c> 完成登录。
    /// </summary>
    [EndpointSummary("登录")]
    [EndpointDescription("启用 MFA 的账号返回 requiresTwoFactor 与一次性 twoFactorToken。")]
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public Task<IActionResult> Login([FromBody] LoginRequest request)
        => ExecuteAsync(() => _accountService.LoginAsync(request, HttpContext.RequestAborted));

    /// <summary>完成两步验证并建立会话。</summary>
    [EndpointSummary("校验两步验证码")]
    [EndpointDescription("用登录返回的 twoFactorToken 换取登录会话。")]
    [HttpPost("2fa/verify")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public Task<IActionResult> VerifyTwoFactor([FromBody] VerifyTwoFactorRequest request)
        => ExecuteAsync(() => _accountService.CompleteTwoFactorAsync(
            request.TwoFactorToken,
            request.Code,
            request.RememberMachine,
            HttpContext.RequestAborted));

    /// <summary>退出登录。</summary>
    [EndpointSummary("退出登录")]
    [EndpointDescription("清除当前 Cookie 会话。")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _accountService.SignOutAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    /// <summary>当前登录用户的档案（含角色与展开后的权限）。</summary>
    [EndpointSummary("查询当前账号档案")]
    [EndpointDescription("含角色，以及由角色展开后的权限列表。")]
    [HttpGet("me")]
    public Task<IActionResult> Me()
        => ExecuteAsync(() => _accountService.GetProfileAsync(HttpContext.RequestAborted));

    /// <summary>修改密码。成功后其他设备的会话会失效（安全戳变更）。</summary>
    [EndpointSummary("修改密码")]
    [EndpointDescription("成功后其他设备的会话会失效（安全戳变更）。")]
    [HttpPost("password")]
    public Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await _accountService.ChangePasswordAsync(request, HttpContext.RequestAborted);
            return result;
        });

    /// <summary>列出该账号可用的两步验证通道。</summary>
    [EndpointSummary("查询两步验证通道")]
    [EndpointDescription("返回该账号已启用的 MFA 通道。")]
    [HttpGet("2fa")]
    public Task<IActionResult> GetTwoFactorProviders()
        => ExecuteAsync(() => _accountService.GetTwoFactorProvidersAsync(HttpContext.RequestAborted));

    /// <summary>开始绑定 Authenticator：返回共享密钥、otpauth URI 与恢复码。</summary>
    [EndpointSummary("开始绑定 Authenticator")]
    [EndpointDescription("返回共享密钥、otpauth URI 与恢复码。")]
    [HttpPost("2fa/setup")]
    public Task<IActionResult> BeginTwoFactorSetup()
        => ExecuteAsync(() => _accountService.BeginTwoFactorSetupAsync(HttpContext.RequestAborted));

    /// <summary>用 Authenticator 的 6 位验证码确认启用 MFA，返回新的恢复码。</summary>
    [EndpointSummary("启用 MFA")]
    [EndpointDescription("用 Authenticator 的 6 位验证码确认，返回新的恢复码。")]
    [HttpPost("2fa/enable")]
    public Task<IActionResult> EnableTwoFactor([FromBody] EnableTwoFactorRequest request)
        => ExecuteAsync(() => _accountService.EnableTwoFactorAsync(request.Code, HttpContext.RequestAborted));

    /// <summary>关闭 MFA 并重置 Authenticator 密钥。</summary>
    [EndpointSummary("关闭 MFA")]
    [EndpointDescription("关闭两步验证，并重置 Authenticator 密钥。")]
    [HttpPost("2fa/disable")]
    public Task<IActionResult> DisableTwoFactor()
        => ExecuteAsync(() => _accountService.DisableTwoFactorAsync(HttpContext.RequestAborted));

    /// <summary>下发一次性验证码（Email / Phone 通道）。</summary>
    [EndpointSummary("发送一次性验证码")]
    [EndpointDescription("支持 Email 与 Phone 通道。")]
    [HttpPost("2fa/send-code")]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public Task<IActionResult> SendTwoFactorCode([FromBody] SendTwoFactorCodeRequest request)
        => ExecuteAsync(() => _accountService.SendTwoFactorCodeAsync(request.Provider, HttpContext.RequestAborted));

    /// <summary>我授权过的应用。</summary>
    [EndpointSummary("查询已授权应用")]
    [EndpointDescription("返回我授权过的客户端及其 Scope。")]
    [HttpGet("consents")]
    public Task<IActionResult> ListConsents()
        => ExecuteAsync(async () =>
        {
            var subject = _currentUser.UserId;
            if (string.IsNullOrEmpty(subject))
            {
                return Application.Common.Result.Failure<IReadOnlyCollection<Application.DTOs.Consents.ConsentDto>>(
                    Application.Common.Error.Unauthorized("未登录。"));
            }

            return await _consentService.ListBySubjectAsync(subject, HttpContext.RequestAborted);
        });

    /// <summary>解除对某个应用的授权（连带撤销其令牌）。</summary>
    [EndpointSummary("解除应用授权")]
    [EndpointDescription("同时撤销该授权的全部令牌。")]
    [HttpDelete("consents/{authorizationId}")]
    public Task<IActionResult> RevokeConsent(string authorizationId)
        => ExecuteAsync(() => _consentService.RevokeAsync(authorizationId, HttpContext.RequestAborted));

    /// <summary>
    /// 邮件链接用的对外基址。优先 <c>AuthHub:PublicBaseUrl</c>，其次 <c>AuthHub:Issuer</c>，
    /// 都未配置时才回退到当前请求（存在 Host 头投毒风险，仅适合本机临时调试）。
    /// </summary>
    private string GetBaseUrl() => PublicBaseUrlResolver.Resolve(_configuration, Request);
}
