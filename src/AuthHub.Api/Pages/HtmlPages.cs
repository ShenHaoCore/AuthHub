using System.Globalization;
using System.Net;
using System.Text;
using AuthHub.Api.Extensions;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.DTOs.Consents;

namespace AuthHub.Api.Pages;

/// <summary>
/// 服务端渲染的极简 HTML 页面（登录 / 两阶段验证 / 同意授权 / 提示）。
///
/// **为什么这几页不用 Razor**（重要，改动前请先读完）：
/// 它们是 OIDC 协议流程的一环 —— 由 <c>AccountController</c> / <c>OpenIddict</c>
/// 在流程中间直接返回，页面内容（returnUrl、请求参数、scope 列表）全部来自
/// 当前这次授权请求。用纯字符串产出 HTML 有三个实际收益：
///   1) 集成测试可以直接断言页面上出现了哪些参数与 scope，不需要解析视图引擎的产物；
///   2) 协议页的失败模式是"登录不了"，越少的编译期魔法越好定位；
///   3) 表单字段名与协议参数一一对应，字符串拼装让这层映射一眼可见。
///
/// **管理后台（/admin/*）走的是另一条路**：Razor Pages（见 Pages/Admin/），
/// 因为那边是大量结构相似的列表 / 表单 / 模态，视图引擎的价值远大于成本。
/// 两条路共用同一套设计令牌（wwwroot/css/authhub-tokens.css），视觉上是一致的。
///
/// 安全上做了三件事：
///   1) 所有动态内容一律 HTML 编码，避免反射型 XSS；
///   2) 表单只提交到同源路径（配合 CSP 的 form-action 'self'）；
///   3) 表单内嵌防伪令牌（__RequestVerificationToken）。
/// </summary>
public static class HtmlPages
{
    public static string LoginPage(
        string returnUrl,
        string requestToken,
        string? error = null,
        string? userName = null,
        IReadOnlyList<(string Name, bool Enabled)>? externalProviders = null,
        WeComScanPanel? weComScan = null)
    {
        var body = new StringBuilder();
        var scan = weComScan ?? WeComScanPanel.Disabled;

        body.Append("<h1>登录 AuthHub</h1>");
        body.Append("<p class=\"subtitle\">使用你的统一账号继续访问应用</p>");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"alert\" role=\"alert\">{E(error)}</div>");
        }

        // 页签：密码登录 | 扫码登录（企业微信）。初始不折叠任何面板 —— 无 JS 时两个面板
        // 上下展开都可用（渐进增强）；authhub-login.js 加载后才把非活跃面板收起来。
        body.Append("<div class=\"login-tab-bar\" role=\"tablist\">");
        body.Append("<button type=\"button\" class=\"login-tab is-active\" role=\"tab\" aria-selected=\"true\" data-login-tab=\"password\">密码登录</button>");
        body.Append("<button type=\"button\" class=\"login-tab\" role=\"tab\" aria-selected=\"false\" data-login-tab=\"scan\">扫码登录</button>");
        body.Append("</div>");

        // ---------- 面板：密码登录 ----------
        body.Append("<div class=\"login-panel\" data-login-panel=\"password\">");
        body.Append($"<form method=\"post\" action=\"/account/login\" autocomplete=\"on\">");
        body.Append(Hidden("__RequestVerificationToken", requestToken));
        body.Append(Hidden("returnUrl", returnUrl));
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"username\">用户名或邮箱</label>");
        body.Append(CultureInfo.InvariantCulture, $"<input id=\"username\" name=\"username\" type=\"text\" autocomplete=\"username\" required autofocus value=\"{E(userName)}\" />");
        body.Append("</div>");
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"password\">密码</label>");
        body.Append("<input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required />");
        body.Append("</div>");
        body.Append("<div class=\"checkbox\">");
        body.Append("<input id=\"rememberMe\" name=\"rememberMe\" type=\"checkbox\" value=\"true\" />");
        body.Append("<label for=\"rememberMe\">在此设备上保持登录</label>");
        body.Append("</div>");
        body.Append("<button class=\"btn-primary\" type=\"submit\">登录</button>");
        body.Append("</form>");

        if (externalProviders is { Count: > 0 })
        {
            // 单个表单 + 多个 submit 按钮：点击哪个按钮，就提交哪个按钮的 provider 值
            body.Append("<div class=\"divider\"><span>或使用以下方式登录</span></div>");
            body.Append("<form method=\"post\" action=\"/account/external-login\" class=\"external-providers\">");
            body.Append(Hidden("__RequestVerificationToken", requestToken));
            body.Append(Hidden("returnUrl", returnUrl));
            foreach (var (provider, enabled) in externalProviders)
            {
                // 企业微信由「扫码登录」页签承载：二维码必须直接可见才可扫，
                // 一个跳得出去但看不到码的按钮没有意义。
                if (provider == WeComLoginService.ProviderName)
                {
                    continue;
                }

                // 未启用的提供商渲染为禁用态：入口可见（预告），但 disabled 按钮不会提交表单；
                // 服务端白名单拦截依然保留，防止绕过页面直接构造 POST。
                var disabled = enabled ? string.Empty : " disabled";
                var suffix = enabled ? string.Empty : "（暂未开放）";
                body.Append(CultureInfo.InvariantCulture,
                    $"<button class=\"btn-external\" type=\"submit\" name=\"provider\" value=\"{E(provider)}\"{disabled}><span class=\"provider-icon\" aria-hidden=\"true\">{ExternalProviderIcon(provider)}</span>使用 {E(ExternalProviderDisplayName(provider))} 登录{suffix}</button>");
            }
            body.Append("</form>");
        }
        body.Append("</div>");

        // ---------- 面板：扫码登录（企业微信） ----------
        body.Append("<div class=\"login-panel\" data-login-panel=\"scan\">");
        if (scan is { Enabled: true, QrIframeUrl: { } qrIframeUrl })
        {
            // 直嵌企业微信官方二维码面板（iframe）。官方 wwLogin.js 本质也是生成同一地址的
            // iframe —— 自嵌省去第三方 JS，符合本站 CSP「无 CDN 引用」约束。扫码确认后
            // iframe 内页面把顶层窗口导航到 redirect_uri（/signin-wecom），与跳转式殊途同归。
            body.Append(CultureInfo.InvariantCulture,
                $"<div class=\"wecom-qr\"><iframe src=\"{E(qrIframeUrl)}\" title=\"企业微信扫码登录\" scrolling=\"no\"></iframe></div>");
            body.Append("<p class=\"hint\">打开企业微信 App，扫码确认即可登录。二维码过期请刷新本页。</p>");
        }
        else
        {
            // 未启用时的占位（与按钮区的禁用态同一预告语义：入口可见，功能未开放）
            body.Append("<div class=\"wecom-qr wecom-qr-disabled\"><span>企业微信扫码登录暂未开放</span></div>");
        }
        body.Append("</div>");

        body.Append("<p class=\"hint\">开发环境内置账号：<code>admin</code> / <code>Admin@12345</code>（管理员），"
                    + "<code>alice</code> / <code>Alice@12345</code>（普通用户）。生产环境请通过管理接口创建账号。</p>");

        return Layout("登录 - AuthHub", body.ToString(), scriptSrc: "/js/authhub-login.js");
    }

    public static string TwoFactorPage(string returnUrl, string requestToken, string? error = null)
    {
        var body = new StringBuilder();

        body.Append("<h1>两步验证</h1>");
        body.Append("<p class=\"subtitle\">请输入身份验证器 App 中的 6 位验证码，或使用一枚恢复码</p>");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"alert\" role=\"alert\">{E(error)}</div>");
        }

        body.Append("<form method=\"post\" action=\"/account/2fa\">");
        body.Append(Hidden("__RequestVerificationToken", requestToken));
        body.Append(Hidden("returnUrl", returnUrl));
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"code\">验证码</label>");
        body.Append("<input id=\"code\" name=\"code\" type=\"text\" inputmode=\"numeric\" autocomplete=\"one-time-code\" required autofocus />");
        body.Append("</div>");
        body.Append("<div class=\"checkbox\">");
        body.Append("<input id=\"rememberMachine\" name=\"rememberMachine\" type=\"checkbox\" value=\"true\" />");
        body.Append("<label for=\"rememberMachine\">在此设备上不再要求验证码</label>");
        body.Append("</div>");
        body.Append("<button class=\"btn-primary\" type=\"submit\">继续</button>");
        body.Append("</form>");

        return Layout("两步验证 - AuthHub", body.ToString());
    }

    /// <summary>
    /// 绑定确认页：外部登录的已验证邮箱匹配到现有本地账号时，让用户显式确认后再建立关联。
    /// 页面上不携带任何身份标识字段 —— 确认 POST 时服务端从外部 Cookie 与数据库重新解析，
    /// 表单参数无法指定"绑定到哪个账号"。
    /// </summary>
    public static string ExternalBindingConfirmPage(
        ExternalBindingView view,
        string requestToken,
        string returnUrl,
        string? error = null)
    {
        var body = new StringBuilder();

        body.Append("<h1>绑定账号</h1>");
        body.Append(CultureInfo.InvariantCulture,
            $"<p class=\"subtitle\">{E(view.Provider)} 账号 <span class=\"client-name\">{E(view.ExternalEmail)}</span> 与你的 AuthHub 账号使用了同一个邮箱，确认后即可用该方式登录：</p>");
        body.Append(CultureInfo.InvariantCulture,
            $"<p>AuthHub 账号：<strong>{E(view.LocalUserName)}</strong>（{E(view.LocalUserEmail)}）</p>");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"alert\" role=\"alert\">{E(error)}</div>");
        }

        var cancelHref = $"/account/external/cancel?returnUrl={Uri.EscapeDataString(returnUrl)}";
        body.Append("<form method=\"post\" action=\"/account/external/confirm\">");
        body.Append(Hidden("__RequestVerificationToken", requestToken));
        body.Append(Hidden("returnUrl", returnUrl));
        body.Append("<div class=\"actions\">");
        body.Append(CultureInfo.InvariantCulture, $"<a class=\"btn-secondary\" href=\"{E(cancelHref)}\">取消</a>");
        body.Append("<button class=\"btn-primary\" type=\"submit\">确认绑定并登录</button>");
        body.Append("</div>");
        body.Append("</form>");

        body.Append("<p class=\"hint\">如果你不认识上面的账号，请选择取消 —— "
                    + "该邮箱可能正被他人使用，必要时请联系管理员。</p>");

        return Layout("绑定账号 - AuthHub", body.ToString());
    }

    /// <summary>
    /// 同意授权页。
    /// 表单会把原始授权请求的全部参数原样回传（连同 submit.Accept / submit.Deny），
    /// 因此 POST 回到 /connect/authorize 时 OpenIddict 仍能解析出完整请求。
    /// </summary>
    public static string ConsentPage(
        string clientDisplayName,
        IReadOnlyCollection<ConsentScopeDescription> scopes,
        IReadOnlyDictionary<string, string> parameters,
        string requestToken,
        string userName)
    {
        var body = new StringBuilder();

        body.Append("<h1>授权确认</h1>");
        body.Append(CultureInfo.InvariantCulture, $"<p class=\"subtitle\">当前登录：{E(userName)}</p>");
        body.Append(CultureInfo.InvariantCulture, $"<p><span class=\"client-name\">{E(clientDisplayName)}</span> 请求访问以下信息：</p>");

        body.Append("<ul class=\"scope-list\">");
        foreach (var scope in scopes)
        {
            body.Append("<li>");
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"scope-name\">{E(scope.DisplayName ?? scope.Name)}</div>");
            if (!string.IsNullOrWhiteSpace(scope.Description))
            {
                body.Append(CultureInfo.InvariantCulture, $"<div>{E(scope.Description)}</div>");
            }
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"scope-code\">{E(scope.Name)}</div>");
            body.Append("</li>");
        }
        body.Append("</ul>");

        body.Append("<form method=\"post\" action=\"/connect/authorize\">");
        body.Append(Hidden("__RequestVerificationToken", requestToken));

        foreach (var (key, value) in parameters)
        {
            if (key.StartsWith("submit.", StringComparison.Ordinal) ||
                key is "__RequestVerificationToken")
            {
                continue;
            }

            body.Append(Hidden(key, value));
        }

        body.Append("<div class=\"actions\">");
        body.Append("<button class=\"btn-secondary\" type=\"submit\" name=\"submit.Deny\" value=\"true\">拒绝</button>");
        body.Append("<button class=\"btn-primary\" type=\"submit\" name=\"submit.Accept\" value=\"true\">同意授权</button>");
        body.Append("</div>");
        body.Append("</form>");

        body.Append("<p class=\"hint\">同意后，该应用将在你未再次确认的情况下继续使用上述权限；"
                    + "你可以随时在账户的“已授权应用”中解除授权。</p>");

        return Layout("授权确认 - AuthHub", body.ToString(), wide: true);
    }

    public static string MessagePage(string title, string message, string? linkHref = null, string? linkText = null)
    {
        var body = new StringBuilder();

        body.Append(CultureInfo.InvariantCulture, $"<h1>{E(title)}</h1>");
        body.Append(CultureInfo.InvariantCulture, $"<p class=\"subtitle\">{E(message)}</p>");

        if (!string.IsNullOrWhiteSpace(linkHref) && !string.IsNullOrWhiteSpace(linkText))
        {
            body.Append(CultureInfo.InvariantCulture, $"<p><a href=\"{E(linkHref)}\">{E(linkText)}</a></p>");
        }

        return Layout($"{title} - AuthHub", body.ToString());
    }

    // ------------------------------------------------------------------ 内部辅助

    // scriptSrc：可选的同源脚本（CSP script-src 'self'，只允许 wwwroot 下的文件；
    // 目前只有登录页的页签切换需要）。
    private static string Layout(string title, string body, bool wide = false, string? scriptSrc = null)
        => $"""
           <!DOCTYPE html>
           <html lang="zh-CN">
           <head>
             <meta charset="utf-8" />
             <meta name="viewport" content="width=device-width, initial-scale=1" />
             <meta name="robots" content="noindex, nofollow" />
             <title>{E(title)}</title>
             <link rel="icon" type="image/svg+xml" href="/favicon.svg" />
             <link rel="stylesheet" href="/css/authhub-auth.css" />
           </head>
           <body>
             <main class="card{(wide ? " wide" : string.Empty)}">
               <div class="brand">
                 <span class="brand-mark">AH</span>
                 <span class="brand-name">AuthHub</span>
               </div>
               {body}
               <div class="footer">统一认证授权中心 · OpenID Connect 1.0 / OAuth 2.0</div>
             </main>
             {(scriptSrc is null ? string.Empty : $"<script src=\"{E(scriptSrc)}\"></script>")}
           </body>
           </html>
           """;

    private static string Hidden(string name, string? value)
        => $"<input type=\"hidden\" name=\"{E(name)}\" value=\"{E(value)}\" />";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>按钮上展示给用户看的提供商名；scheme 名是协议标识，不一定适合直接展示。</summary>
    private static string ExternalProviderDisplayName(string provider) => provider switch
    {
        WeComLoginService.ProviderName => "企业微信",
        _ => provider
    };

    /// <summary>
    /// 提供商品牌图标（内联 SVG，不是外链图片）：内联 SVG 是文档自身的一部分，
    /// 不产生任何网络请求，天然满足 CSP 约束 —— 本站 img-src 只有 'self' data:，
    /// 从 CDN 拉图标既会被拦又违反「无站外资源」约定。
    /// GitHub 用单色路径 fill=currentColor 跟随文字色；Google 的 G 是四色品牌标志，
    /// 单色会失去辨识度，写死官方色值。未知提供商返回空串（无官方图形就不放占位）。
    /// </summary>
    private static string ExternalProviderIcon(string provider) => provider switch
    {
        "GitHub" => """
            <svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8Z"/></svg>
            """,
        "Google" => """
            <svg viewBox="0 0 48 48" aria-hidden="true"><path fill="#EA4335" d="M24 9.5c3.54 0 6.71 1.22 9.21 3.6l6.85-6.85C35.9 2.38 30.47 0 24 0 14.62 0 6.51 5.38 2.56 13.22l7.98 6.19C12.43 13.72 17.74 9.5 24 9.5z"/><path fill="#4285F4" d="M46.98 24.55c0-1.57-.15-3.09-.38-4.55H24v9.02h12.94c-.58 2.96-2.26 5.48-4.78 7.18l7.73 6c4.51-4.18 7.09-10.36 7.09-17.65z"/><path fill="#FBBC05" d="M10.53 28.59c-.48-1.45-.76-2.99-.76-4.59s.27-3.14.76-4.59l-7.98-6.19C.92 16.46 0 20.12 0 24c0 3.88.92 7.54 2.56 10.78l7.97-6.19z"/><path fill="#34A853" d="M24 48c6.48 0 11.93-2.13 15.89-5.81l-7.73-6c-2.15 1.45-4.92 2.3-8.16 2.3-6.26 0-11.57-4.22-13.47-9.91l-7.98 6.19C6.51 42.62 14.62 48 24 48z"/></svg>
            """,
        _ => string.Empty
    };
}

/// <summary>登录页「扫码登录」页签的渲染参数（企业微信二维码面板）。</summary>
/// <param name="Enabled">企业微信是否启用；false 时页签内渲染「暂未开放」占位而非二维码。</param>
/// <param name="QrIframeUrl">
/// 二维码面板 iframe 的地址（企业微信官方扫码页，含限时 state 与本站回调），仅启用时有值。
/// state 的双提交 Cookie 由调用方（控制器）随本页响应一起下发。
/// </param>
public sealed record WeComScanPanel(bool Enabled, string? QrIframeUrl)
{
    /// <summary>未启用时的占位渲染参数。</summary>
    public static readonly WeComScanPanel Disabled = new(false, null);
}
