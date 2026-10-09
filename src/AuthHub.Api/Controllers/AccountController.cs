using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Api.Extensions;
using AuthHub.Api.Pages;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthHub.Api.Controllers;

/// <summary>
/// 浏览器交互页面：登录页、两步验证页、提示页。
///
/// 这些页面是 OIDC 授权流程的一部分（授权端点会把未登录用户重定向到 /account/login），
/// 因此使用表单提交 + 防伪令牌，而不是 JSON API。
/// 纯 API 调用方请使用 /api/account/*（见 AccountApiController）。
///
/// 该控制器不加 [ApiController]：需要返回 HTML 页面与 302 跳转。
/// </summary>
/// <remarks>
/// <b>本控制器不进 API 文档</b>（<c>[ApiExplorerSettings(IgnoreApi = true)]</c>）。
/// 它产出的是浏览器页面，调用方在文档里看到 /account/login、/account/2fa 这类端点
/// 只会困惑 —— 它们既不能从 Scalar 直接发起（要会话 + 防伪令牌），
/// 也不是给程序化调用方用的。需要程序化调用请走 <c>/api/account/*</c>（AccountApiController）。
///
/// 为什么不在 <c>OpenApiExtensions</c> 里改那个可见性约定来实现「只收录 API」：
/// 那个约定是专门给 <c>/connect/*</c> 用的（那两个控制器同样没有 [ApiController]，
/// 但必须出现在文档里）。框架的 <c>ApiVisibilityConvention</c> 只在控制器与动作的
/// <c>IsVisible</c> **都为 null** 时才点亮，所以这里的显式 false 不会被它覆盖 ——
/// 两件事互不干扰，各自留在自己该在的地方。
/// </remarks>
[Tags("登录与提示页")]
[Route("account")]
[ApiExplorerSettings(IgnoreApi = true)]
public class AccountController : Controller
{
    /// <summary>保存“已通过密码校验、等待第二因子”的临时票据。</summary>
    private const string PendingTwoFactorCookie = "authhub.2fa.pending";

    private readonly IAccountService _accountService;
    private readonly IAntiforgery _antiforgery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IAccountService accountService,
        IAntiforgery antiforgery,
        IConfiguration configuration,
        ILogger<AccountController> logger)
    {
        _accountService = accountService;
        _antiforgery = antiforgery;
        _configuration = configuration;
        _logger = logger;
    }

    // ------------------------------------------------------------------ 登录

    [EndpointSummary("登录页")]
    [EndpointDescription("浏览器表单页；未登录访问受保护页面时会跳到这里。")]
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl = null, [FromQuery] string? error = null)
    {
        var safeReturnUrl = SafeReturnUrl(returnUrl);
        return Html(HtmlPages.LoginPage(safeReturnUrl, IssueAntiforgeryToken(), error));
    }

    [EndpointSummary("提交登录")]
    [EndpointDescription("表单提交；成功 302 回 returnUrl，启用 MFA 则转两步验证页。")]
    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> Login(
        [FromForm] string username,
        [FromForm] string password,
        [FromForm] bool rememberMe = false,
        [FromForm] string? returnUrl = null)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var safeReturnUrl = SafeReturnUrl(returnUrl);

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Html(HtmlPages.LoginPage(safeReturnUrl, IssueAntiforgeryToken(), "请输入用户名和密码。", username));
        }

        var result = await _accountService.LoginAsync(new LoginRequest(username, password, rememberMe), cancellationToken);

        if (result.IsFailure)
        {
            _logger.LogInformation("登录页登录失败：{Reason}", result.Error.Message);
            return Html(HtmlPages.LoginPage(safeReturnUrl, IssueAntiforgeryToken(), result.Error.Message, username));
        }

        var login = result.Value;

        if (login.RequiresTwoFactor)
        {
            Response.Cookies.Append(PendingTwoFactorCookie, login.TwoFactorToken ?? string.Empty, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10)
            });

            return Redirect($"/account/2fa?returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
        }

        return Redirect(safeReturnUrl);
    }

    // ------------------------------------------------------------------ 两步验证

    [EndpointSummary("两步验证页")]
    [EndpointDescription("浏览器表单页；没有待验证票据时回到登录页。")]
    [HttpGet("2fa")]
    public IActionResult TwoFactor([FromQuery] string? returnUrl = null, [FromQuery] string? error = null)
    {
        if (!Request.Cookies.ContainsKey(PendingTwoFactorCookie))
        {
            // 没有待验证票据（直接访问或票据过期），回到登录页重新走一遍
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}");
        }

        return Html(HtmlPages.TwoFactorPage(SafeReturnUrl(returnUrl), IssueAntiforgeryToken(), error));
    }

    [EndpointSummary("提交两步验证码")]
    [EndpointDescription("校验通过后建立登录会话。")]
    [HttpPost("2fa")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> TwoFactor(
        [FromForm] string code,
        [FromForm] bool rememberMachine = false,
        [FromForm] string? returnUrl = null)
    {
        var safeReturnUrl = SafeReturnUrl(returnUrl);
        var ticket = Request.Cookies[PendingTwoFactorCookie];

        if (string.IsNullOrEmpty(ticket))
        {
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
        }

        var result = await _accountService.CompleteTwoFactorAsync(ticket, code, rememberMachine, HttpContext.RequestAborted);

        // 无论成功失败都销毁票据，避免被重复使用
        Response.Cookies.Delete(PendingTwoFactorCookie);

        if (result.IsFailure)
        {
            if (result.Error.Type == Application.Common.ErrorType.LockedOut)
            {
                return Redirect($"/account/login?error={Uri.EscapeDataString(result.Error.Message)}");
            }

            // 验证失败需要重新发起登录（票据已销毁）
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(safeReturnUrl)}&error={Uri.EscapeDataString(result.Error.Message)}");
        }

        return Redirect(safeReturnUrl);
    }

    // ------------------------------------------------------------------ 登出

    /// <summary>
    /// 退出登录（管理后台顶栏的用户菜单）。
    ///
    /// 与 <c>/connect/logout</c> 的区别：那个是 OIDC 的 RP-Initiated Logout 端点，
    /// 会按 post_logout_redirect_uri 白名单把用户送回客户端；这里只是清掉本服务的会话，
    /// 然后停在"已退出"提示页。
    ///
    /// 真正的登出只接受 POST 而不是 GET：GET 登出可以被任意页面用一张图片触发。
    /// 防伪令牌为手动校验而不用 <c>[ValidateAntiForgeryToken]</c>：
    /// 会话已结束时（重复点击、返回键重放 POST）直接落到提示页，
    /// 不让令牌与匿名身份不匹配产生一个突兀的 400 空白页。
    /// </summary>
    [EndpointSummary("退出登录（页面）")]
    [EndpointDescription("清除会话后停在“已退出”提示页；会话已结束时幂等落到同一页。")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        // 不能读 User：默认认证方案是 OpenIddict 令牌校验（见 AuthenticationExtensions），
        // 浏览器页面上的 User 恒为匿名。会话状态必须向 Identity 的 Cookie 方案查询 ——
        // 与授权端点判断 SSO 会话是同一款写法（见 AuthorizationController.Authorize）。
        var session = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!session.Succeeded)
            return Redirect("/account/loggedout"); // 重复点击 / 返回键重放：幂等落到提示页

        // 防伪令牌按“当时的登录身份”绑定（令牌里带了用户名）。页面渲染走的是 Identity 会话身份，
        // 而这里的默认 User 是匿名 —— 直接校验必然报 "meant for a different claims-based user"。
        // 校验前把 User 换成会话 principal，与渲染端对齐；校验完恢复，不影响后续管线。
        var originalUser = HttpContext.User;
        HttpContext.User = session.Principal!;
        try
        {
            await _antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException ex)
        {
            _logger.LogWarning(ex, "登出请求防伪校验失败（可能是 CSRF，或令牌与当前身份绑定不一致）");
            return BadRequest(); // 带会话但令牌无效：按可疑请求对待，维持 400
        }
        finally
        {
            HttpContext.User = originalUser;
        }

        await _accountService.SignOutAsync(HttpContext.RequestAborted);
        Response.Cookies.Delete(PendingTwoFactorCookie);

        return Redirect("/account/loggedout");
    }

    /// <summary>
    /// GET <c>/account/logout</c>：不做任何登出动作（登出只走上面的 POST），
    /// 只把直接访问 / 从历史记录回访的人引到有意义的地方，避免 405 空白页。
    /// 会话查询同样走 Identity Cookie 方案（原因见上面 Logout 的注释）。
    /// </summary>
    [HttpGet("logout")]
    [EndpointSummary("退出登录入口（GET 仅跳转）")]
    [EndpointDescription("不产生任何会话变化；已登录回后台首页，未登录回登录页。")]
    public async Task<IActionResult> LogoutGet()
    {
        var session = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        return Redirect(session.Succeeded ? "/admin" : "/account/login");
    }

    // ------------------------------------------------------------------ 忘记密码 / 重置密码

    [EndpointSummary("忘记密码页")]
    [EndpointDescription("输入邮箱提交后发送重置链接；无论邮箱是否存在都给同一提示。")]
    [HttpGet("forgot-password")]
    public IActionResult ForgotPassword([FromQuery] string? error = null)
        => Html(HtmlPages.ForgotPasswordPage(IssueAntiforgeryToken(), error));

    /// <summary>
    /// 提交忘记密码：调用服务发重置链接，然后跳到成功提示页。
    /// 无论邮箱是否存在都跳同一页，防用户名枚举。
    /// </summary>
    [HttpPost("forgot-password")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> ForgotPasswordPost([FromForm] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Html(HtmlPages.ForgotPasswordPage(IssueAntiforgeryToken(), "请输入邮箱。", email));
        }

        await _accountService.RequestPasswordResetAsync(email.Trim(), GetBaseUrl(), HttpContext.RequestAborted);
        return Redirect("/account/forgot-password/sent");
    }

    [EndpointSummary("重置链接已发送提示")]
    [EndpointDescription("静态提示页，引导用户查收邮件。")]
    [HttpGet("forgot-password/sent")]
    public IActionResult ForgotPasswordSent()
        => Html(HtmlPages.MessagePage(
            "重置链接已发送",
            "如该邮箱已注册，重置密码的链接已发送到你的邮箱，1 小时内有效。请查收邮件并点击链接完成重置。",
            "/account/login",
            "返回登录"));

    /// <summary>
    /// 重置密码页（GET）：从邮件链接带 email + token 进入，渲染输入新密码的表单。
    /// 先不校验 token，让用户填完密码提交时再校验——避免 GET 请求把 token 当查询串
    /// 传给后台时就触发失败分支，体验上更顺。
    /// </summary>
    [EndpointSummary("重置密码页")]
    [EndpointDescription("从邮件链接进入，输入新密码。")]
    [HttpGet("reset-password")]
    public IActionResult ResetPassword([FromQuery] string email, [FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
        {
            return Html(HtmlPages.MessagePage(
                "链接无效",
                "重置链接不完整，请重新从邮件打开，或重新申请重置。",
                "/account/forgot-password",
                "重新申请"));
        }

        return Html(HtmlPages.ResetPasswordPage(email, token, IssueAntiforgeryToken()));
    }

    [HttpPost("reset-password")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> ResetPasswordPost(
        [FromForm] string email,
        [FromForm] string token,
        [FromForm] string newPassword)
    {
        var result = await _accountService.ResetPasswordAsync(email, token, newPassword, HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            // 失败时把 token 原样塞回表单，让用户可以重试（可能只是密码强度不够）
            return Html(HtmlPages.ResetPasswordPage(email, token, IssueAntiforgeryToken(), result.Error.Message));
        }

        return Redirect("/account/reset-password/done");
    }

    [EndpointSummary("密码重置成功")]
    [EndpointDescription("静态提示页，引导用户用新密码登录。")]
    [HttpGet("reset-password/done")]
    public IActionResult ResetPasswordDone()
        => Html(HtmlPages.MessagePage(
            "密码已重置",
            "你的新密码已生效，所有其他设备上的登录已被登出。请使用新密码登录。",
            "/account/login",
            "去登录"));

    // ------------------------------------------------------------------ 邮箱确认

    /// <summary>
    /// 消费邮箱确认链接（GET，用户从邮件点进来）。成功/失败都落到提示页。
    /// </summary>
    [EndpointSummary("确认邮箱")]
    [EndpointDescription("消费邮件里的确认令牌；结果以提示页展示。")]
    [HttpGet("confirm-email")]
    public async Task<IActionResult> ConfirmEmail([FromQuery] string userId, [FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
        {
            return Html(HtmlPages.MessagePage(
                "确认链接无效",
                "确认链接不完整，请重新申请。",
                "/account/resend-confirmation",
                "重新发送确认邮件"));
        }

        var result = await _accountService.ConfirmEmailAsync(userId, token, HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            return Html(HtmlPages.MessagePage(
                "确认失败",
                result.Error.Message,
                "/account/resend-confirmation",
                "重新发送确认邮件"));
        }

        return Html(HtmlPages.MessagePage(
            "邮箱已确认",
            "你的邮箱已成功确认，可以正常使用全部功能。",
            "/account/login",
            "去登录"));
    }

    [EndpointSummary("重发确认邮件页")]
    [EndpointDescription("输入邮箱后重发确认链接；无论是否存在都给同一提示。")]
    [HttpGet("resend-confirmation")]
    public IActionResult ResendConfirmation([FromQuery] string? error = null)
        => Html(HtmlPages.ResendConfirmationPage(IssueAntiforgeryToken(), error));

    [HttpPost("resend-confirmation")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> ResendConfirmationPost([FromForm] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Html(HtmlPages.ResendConfirmationPage(IssueAntiforgeryToken(), "请输入邮箱。", email));
        }

        await _accountService.ResendEmailConfirmationAsync(email.Trim(), GetBaseUrl(), HttpContext.RequestAborted);
        return Redirect("/account/resend-confirmation/sent");
    }

    [EndpointSummary("确认邮件已发送提示")]
    [EndpointDescription("静态提示页，引导用户查收确认邮件。")]
    [HttpGet("resend-confirmation/sent")]
    public IActionResult ResendConfirmationSent()
        => Html(HtmlPages.MessagePage(
            "确认邮件已发送",
            "如该邮箱已注册且尚未确认，确认链接已发送到你的邮箱，1 小时内有效。请查收邮件并点击链接完成确认。",
            "/account/login",
            "返回登录"));

    // ------------------------------------------------------------------ 提示页

    [EndpointSummary("已退出提示页")]
    [EndpointDescription("静态提示页，引导重新登录。")]
    [HttpGet("loggedout")]
    public IActionResult LoggedOut()
        => Html(HtmlPages.MessagePage("已退出登录", "你的 AuthHub 会话已结束。关闭浏览器标签页，或重新登录。", "/account/login", "重新登录"));

    [EndpointSummary("无权访问提示页")]
    [EndpointDescription("已登录但缺少所需权限时跳转到这里。")]
    [HttpGet("denied")]
    public IActionResult Denied()
        => Html(HtmlPages.MessagePage("无权访问", "当前账号没有访问该资源的权限。", "/", "返回首页"));

    // ------------------------------------------------------------------ 内部辅助

    private static ContentResult Html(string html, int statusCode = StatusCodes.Status200OK)
        => new()
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = statusCode
        };

    private string IssueAntiforgeryToken()
        => _antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

    /// <summary>
    /// 只允许跳回站内地址，防止开放重定向（Open Redirect）：
    /// 攻击者可能构造 /account/login?returnUrl=https://evil.com 诱骗用户登录后跳转外部站点。
    /// </summary>
    private string SafeReturnUrl(string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";

    /// <summary>
    /// 邮件链接用的对外基址。优先 <c>AuthHub:PublicBaseUrl</c>，其次 <c>AuthHub:Issuer</c>，
    /// 都未配置时才回退到当前请求（存在 Host 头投毒风险，仅适合本机临时调试）。
    /// </summary>
    private string GetBaseUrl() => PublicBaseUrlResolver.Resolve(_configuration, Request);
}
