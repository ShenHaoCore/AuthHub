using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Api.Extensions;
using AuthHub.Api.Pages;
using Microsoft.AspNetCore.Antiforgery;
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
[Tags("登录与提示页")]
[Route("account")]
public class AccountController : Controller
{
    /// <summary>保存“已通过密码校验、等待第二因子”的临时票据。</summary>
    private const string PendingTwoFactorCookie = "authhub.2fa.pending";

    private readonly IAccountService _accountService;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IAccountService accountService,
        IAntiforgery antiforgery,
        ILogger<AccountController> logger)
    {
        _accountService = accountService;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    // ------------------------------------------------------------------ 登录

    [EndpointSummary("登录页")]
    [EndpointDescription("浏览器表单页；未登录访问受保护页面时会跳到这里。")]
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl = null, [FromQuery] string? error = null)
        => Html(HtmlPages.LoginPage(SafeReturnUrl(returnUrl), IssueAntiforgeryToken(), error));

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
    /// 用 POST + 防伪令牌而不是 GET：GET 登出可以被任意页面用一张图片触发。
    /// </summary>
    [EndpointSummary("退出登录（页面）")]
    [EndpointDescription("清除会话后停在“已退出”提示页。")]
    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _accountService.SignOutAsync(HttpContext.RequestAborted);
        Response.Cookies.Delete(PendingTwoFactorCookie);

        return Redirect("/account/loggedout");
    }

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
}
