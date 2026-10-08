using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Api.Extensions;
using AuthHub.Api.Pages;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

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

    /// <summary>企业微信扫码登录的 state 双提交 Cookie（一次性，回调校验后即删）。</summary>
    private const string WeComStateCookie = "authhub.wecom.state";

    private readonly IAccountService _accountService;
    private readonly IAntiforgery _antiforgery;
    private readonly IOptions<ExternalLoginOptions> _externalOptions;
    private readonly WeComLoginService _weCom;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IAccountService accountService,
        IAntiforgery antiforgery,
        IOptions<ExternalLoginOptions> externalOptions,
        WeComLoginService weCom,
        ILogger<AccountController> logger)
    {
        _accountService = accountService;
        _antiforgery = antiforgery;
        _externalOptions = externalOptions;
        _weCom = weCom;
        _logger = logger;
    }

    // ------------------------------------------------------------------ 登录

    [EndpointSummary("登录页")]
    [EndpointDescription("浏览器表单页；未登录访问受保护页面时会跳到这里。")]
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl = null, [FromQuery] string? error = null)
    {
        var safeReturnUrl = SafeReturnUrl(returnUrl);
        return Html(HtmlPages.LoginPage(
            safeReturnUrl,
            IssueAntiforgeryToken(),
            error,
            externalProviders: _externalOptions.Value.AllProviders,
            weComScan: BuildWeComScanPanel(safeReturnUrl)));
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
            return Html(HtmlPages.LoginPage(safeReturnUrl, IssueAntiforgeryToken(), "请输入用户名和密码。", username, _externalOptions.Value.AllProviders, BuildWeComScanPanel(safeReturnUrl)));
        }

        var result = await _accountService.LoginAsync(new LoginRequest(username, password, rememberMe), cancellationToken);

        if (result.IsFailure)
        {
            _logger.LogInformation("登录页登录失败：{Reason}", result.Error.Message);
            return Html(HtmlPages.LoginPage(safeReturnUrl, IssueAntiforgeryToken(), result.Error.Message, username, _externalOptions.Value.AllProviders, BuildWeComScanPanel(safeReturnUrl)));
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

    // ------------------------------------------------------------------ 第三方登录

    /// <summary>
    /// 发起第三方登录（登录页的 GitHub / Google 按钮）。
    ///
    /// 刻意是 POST + 防伪令牌而不是 GET 链接：GET 发起登录挑战可以被任何第三方页面
    /// 用一张图片触发（登录 CSRF —— 把受害者的浏览器登录进攻击者的账号）。
    /// </summary>
    [EndpointSummary("发起第三方登录")]
    [EndpointDescription("表单提交；把浏览器重定向到对应提供商的授权页。仅接受已启用的提供商。")]
    [HttpPost("external-login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> ExternalLogin([FromForm] string provider, [FromForm] string? returnUrl = null)
    {
        var safeReturnUrl = SafeReturnUrl(returnUrl);

        // 白名单取自与登录页按钮同一份配置：未启用的提供商连按钮都不渲染，直发请求也进不来
        if (string.IsNullOrWhiteSpace(provider) || !_externalOptions.Value.EnabledProviders.Contains(provider))
        {
            return Redirect(LoginErrorUrl(safeReturnUrl, "该登录方式未启用。"));
        }

        // 企业微信由登录页「扫码登录」页签承载（二维码必须直接可见才可扫，跳转式 challenge
        // 不适用）；直接构造的 WeCom POST 一律带回登录页，扫码入口就在那里。
        if (provider == WeComLoginService.ProviderName)
        {
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
        }

        // 上一次外部登录残留的外部身份 Cookie 必须清掉：A 提供商流程中断后马上点 B，
        // B 的回调页可能读到 A 的身份，绑定确认页就会张冠李戴。
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        // 授权结束后提供商按 RedirectUri 回跳到 /account/external-callback；
        // returnUrl 挂在回调地址的查询串上，由回调端点再校验一次。
        var redirectUrl = $"/account/external-callback?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
        var properties = new AuthenticationProperties { RedirectUri = redirectUrl };
        properties.Items["LoginProvider"] = provider; // 回调时从外部 Cookie 读回，键与 GetExternalLoginInfoAsync 对齐

        return Challenge(properties, provider);
    }

    /// <summary>
    /// 构造登录页「扫码登录」页签的渲染参数。启用时生成限时 state（内嵌 returnUrl）并写
    /// 双提交 Cookie（与 OAuth handler 的 correlation 机制同构）；二维码 iframe 地址即
    /// 企业微信官方扫码页，参数里带着本站回调。
    /// 每次渲染登录页都换新 state，旧二维码随之失效 —— 与「重复发起 challenge 使旧
    /// correlation 失效」的 OAuth 行为一致，刷新页面即可重获新码。
    /// </summary>
    private WeComScanPanel BuildWeComScanPanel(string returnUrl)
    {
        if (!_externalOptions.Value.EnabledProviders.Contains(WeComLoginService.ProviderName))
        {
            return WeComScanPanel.Disabled;
        }

        // redirect_uri 必须是绝对地址（企业微信按可信域名精确匹配）。用当前请求的
        // Scheme/Host 拼装：ForwardedHeaders 已在管线最前面还原了公网入口的协议与域名。
        var callbackUrl = $"{Request.Scheme}://{Request.Host}{WeComLoginService.CallbackPath}";
        var qrIframeUrl = _weCom.CreateChallengeUrl(returnUrl, callbackUrl, out var state);
        Response.Cookies.Append(WeComStateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });
        return new WeComScanPanel(true, qrIframeUrl);
    }

    /// <summary>
    /// 企业微信扫码确认后的回跳端点（自建应用 SSO，文档参数名 auth_code 而非 code）。
    ///
    /// 职责只有一个：把企业微信协议转换成与其它提供商一致的中间态（Identity 外部 Cookie
    /// + LoginProvider），然后转交 /account/external-callback 走统一的账号匹配/绑定/建号链路。
    /// state 的双提交校验（Cookie ↔ 查询串）替代防伪令牌 —— 外部请求不可能携带本站令牌。
    /// </summary>
    [EndpointSummary("企业微信扫码登录回调")]
    [EndpointDescription("校验 state 相关性后用 auth_code 换成员身份，转交统一的外部登录回调。")]
    [HttpGet("/signin-wecom")]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> WeComCallback(
        [FromQuery(Name = "auth_code")] string? authCode,
        [FromQuery] string? state)
    {
        // 未启用时也拒绝（与 external-login 的白名单同口径）：路由存在但流程进不来。
        // 此刻 returnUrl 还锁在 state 里没法安全解出，错误页一律回登录页默认地址。
        if (!_externalOptions.Value.EnabledProviders.Contains(WeComLoginService.ProviderName))
        {
            return Redirect(LoginErrorUrl("/", "该登录方式未启用。"));
        }

        // state Cookie 是一次性的：无论校验成败都先删掉，防止旧票据被反复试探
        var cookieState = Request.Cookies[WeComStateCookie];
        Response.Cookies.Delete(WeComStateCookie);
        if (!_weCom.TryValidateState(cookieState, state, out var stateReturnUrl))
        {
            return Redirect(LoginErrorUrl("/", "登录状态校验失败或已过期，请重新发起扫码登录。"));
        }

        var safeReturnUrl = SafeReturnUrl(stateReturnUrl);

        if (string.IsNullOrWhiteSpace(authCode))
        {
            return Redirect(LoginErrorUrl(safeReturnUrl, "企业微信回调缺少授权码，请重新扫码。"));
        }

        var principalResult = await _weCom.CreatePrincipalAsync(authCode, HttpContext.RequestAborted);
        if (principalResult.IsFailure)
        {
            return Redirect(LoginErrorUrl(safeReturnUrl, principalResult.Error.Message));
        }

        // 转成与 GitHub/Google 一致的中间态：外部 Cookie + LoginProvider + RedirectUri，
        // 后续账号匹配、绑定确认、自动建号全部走既有链路
        var redirectUrl = $"/account/external-callback?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
        var properties = new AuthenticationProperties { RedirectUri = redirectUrl };
        properties.Items["LoginProvider"] = WeComLoginService.ProviderName;

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        await HttpContext.SignInAsync(IdentityConstants.ExternalScheme, principalResult.Value, properties);
        return Redirect(redirectUrl);
    }

    /// <summary>
    /// 提供商授权后的回跳端点。
    ///
    /// 防伪令牌在这里不适用（外部请求无法携带本站令牌）；请求真实性由 OAuth 协议的
    /// state 相关性校验保证 —— 处理器只接受携带着本站签发 state 的回调。
    /// </summary>
    [EndpointSummary("第三方登录回调")]
    [EndpointDescription("提供商授权后回跳；已绑定或新建账号直接登录，匹配到本地账号转绑定确认页。")]
    [HttpGet("external-callback")]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> ExternalCallback([FromQuery] string? returnUrl = null)
    {
        var safeReturnUrl = SafeReturnUrl(returnUrl);

        var info = await GetExternalLoginInfoAsync();
        if (info is null)
        {
            // 直接访问 / 相关 Cookie 过期 / 用户在提供商侧取消 —— 统一回登录页带提示。
            // 还没有可归属的本地身份，这里不产生审计事件。
            return Redirect(LoginErrorUrl(safeReturnUrl, "外部登录未完成或已超时，请重试。"));
        }

        var result = await _accountService.SignInWithExternalLoginAsync(info, HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            return Redirect(LoginErrorUrl(safeReturnUrl, result.Error.Message));
        }

        if (result.Value.Status == ExternalLoginStatus.BindingConfirmationRequired)
        {
            // 外部身份 Cookie 刻意保留：确认页的 POST 靠它读回外部身份
            return Redirect($"/account/external/confirm?returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return Redirect(safeReturnUrl);
    }

    [EndpointSummary("第三方账号绑定确认页")]
    [EndpointDescription("外部邮箱匹配到本地账号时展示；确认后把外部登录与该账号建立绑定。")]
    [HttpGet("external/confirm")]
    public async Task<IActionResult> ExternalConfirm([FromQuery] string? returnUrl = null, [FromQuery] string? error = null)
    {
        var safeReturnUrl = SafeReturnUrl(returnUrl);

        var info = await GetExternalLoginInfoAsync();
        if (info is null)
        {
            return Redirect(LoginErrorUrl(safeReturnUrl, "绑定会话已过期，请重新发起第三方登录。"));
        }

        var view = await _accountService.GetExternalBindingViewAsync(info, HttpContext.RequestAborted);
        if (view.IsFailure)
        {
            return Redirect(LoginErrorUrl(safeReturnUrl, view.Error.Message));
        }

        return Html(HtmlPages.ExternalBindingConfirmPage(view.Value, IssueAntiforgeryToken(), safeReturnUrl, error));
    }

    [EndpointSummary("确认绑定第三方账号")]
    [EndpointDescription("把外部登录与本地账号建立绑定并建立会话；失败回到确认页可重试。")]
    [HttpPost("external/confirm")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    public async Task<IActionResult> ExternalConfirmPost([FromForm] string? returnUrl = null)
    {
        var safeReturnUrl = SafeReturnUrl(returnUrl);

        var info = await GetExternalLoginInfoAsync();
        if (info is null)
        {
            return Redirect(LoginErrorUrl(safeReturnUrl, "绑定会话已过期，请重新发起第三方登录。"));
        }

        var result = await _accountService.ConfirmExternalBindingAsync(info, HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            // 外部身份 Cookie 仍在，回确认页可在同一次授权内重试（如绑定冲突时用户换账号处理）
            return Redirect($"/account/external/confirm?returnUrl={Uri.EscapeDataString(safeReturnUrl)}&error={Uri.EscapeDataString(result.Error.Message)}");
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return Redirect(safeReturnUrl);
    }

    /// <summary>
    /// 取消绑定 / 登录：只清掉待确认的外部身份中间态，不碰现有会话 ——
    /// 因此是 GET 也无 CSRF 风险（最坏后果只是"少点一次确认"）。
    /// </summary>
    [EndpointSummary("取消第三方登录/绑定")]
    [EndpointDescription("清掉待确认的外部身份后回登录页；不影响已建立的会话。")]
    [HttpGet("external/cancel")]
    public async Task<IActionResult> ExternalCancel([FromQuery] string? returnUrl = null)
    {
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}");
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

    /// <summary>
    /// 从 Identity 的外部 Cookie 读回提供商返回的身份，组装成 <see cref="ExternalLoginInfo"/>。
    ///
    /// 不用 <c>SignInManager.GetExternalLoginInfoAsync()</c>：它按 Identity 配置的 UserIdClaimType
    ///（本项目已对齐成 sub，见 IdentityExtensions）找 ProviderKey，而这里需要的是「提供商侧」
    /// 的稳定 ID。两家处理器的 NameIdentifier 声明都映射自提供商用户 ID（GitHub 的 id /
    /// Google 的 sub），按 NameIdentifier → sub 的顺序取最稳。
    /// </summary>
    private async Task<ExternalLoginInfo?> GetExternalLoginInfoAsync()
    {
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        if (!result.Succeeded || result.Principal is null || result.Properties is null)
        {
            return null;
        }

        if (!result.Properties.Items.TryGetValue("LoginProvider", out var provider) || string.IsNullOrEmpty(provider))
        {
            return null;
        }

        var providerKey = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? result.Principal.FindFirstValue("sub");
        if (string.IsNullOrEmpty(providerKey))
        {
            return null;
        }

        return new ExternalLoginInfo(
            result.Principal,
            provider,
            providerKey,
            result.Principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty);
    }

    /// <summary>带错误提示的登录页跳转地址（错误经 URL 编码，防注入页面文案）。</summary>
    private static string LoginErrorUrl(string returnUrl, string message)
        => $"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error={Uri.EscapeDataString(message)}";

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
