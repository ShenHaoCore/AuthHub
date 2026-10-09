using System.Globalization;
using System.Net;
using System.Text;
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
        string? userName = null)
    {
        var body = new StringBuilder();

        body.Append("<h1>登录</h1>");
        body.Append("<p class=\"subtitle\">使用你的统一账号继续访问应用</p>");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"alert\" role=\"alert\">{E(error)}</div>");
        }

        body.Append("<form method=\"post\" action=\"/account/login\" autocomplete=\"on\">");
        body.Append(Hidden("__RequestVerificationToken", requestToken));
        body.Append(Hidden("returnUrl", returnUrl));
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"username\">用户名或邮箱</label>");
        body.Append(CultureInfo.InvariantCulture, $"<input id=\"username\" name=\"username\" type=\"text\" autocomplete=\"username\" required autofocus value=\"{E(userName)}\" />");
        body.Append("</div>");
        body.Append("<div class=\"field\">");
        body.Append("<div class=\"field-label-row\">");
        body.Append("<label for=\"password\">密码</label>");
        body.Append("<a class=\"forgot-link\" href=\"/account/forgot-password\">忘记密码？</a>");
        body.Append("</div>");
        body.Append("<input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required />");
        body.Append("</div>");
        body.Append("<div class=\"checkbox\">");
        body.Append("<input id=\"rememberMe\" name=\"rememberMe\" type=\"checkbox\" value=\"true\" />");
        body.Append("<label for=\"rememberMe\">在此设备上保持登录</label>");
        body.Append("</div>");
        body.Append("<button class=\"btn-primary\" type=\"submit\">登录</button>");
        body.Append("</form>");

        body.Append("<p class=\"hint\">开发环境内置账号：<code>admin</code> / <code>Admin@12345</code>（管理员），"
                    + "<code>alice</code> / <code>Alice@12345</code>（普通用户）。生产环境请通过管理接口创建账号。</p>");

        return Layout("登录 - AuthHub", body.ToString());
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

    // ------------------------------------------------------------------ 忘记密码 / 重置密码

    /// <summary>
    /// 忘记密码页：输入邮箱提交后，向该邮箱发送重置链接。
    /// 提交后无论邮箱是否存在都提示"如邮箱已注册，重置链接已发送"——避免枚举账号。
    /// </summary>
    public static string ForgotPasswordPage(string requestToken, string? error = null, string? email = null)
    {
        var body = new StringBuilder();

        body.Append("<h1>重置密码</h1>");
        body.Append("<p class=\"subtitle\">输入注册时的邮箱，我们会发送重置链接到该邮箱。</p>");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"alert\" role=\"alert\">{E(error)}</div>");
        }

        body.Append("<form method=\"post\" action=\"/account/forgot-password\">");
        body.Append(Hidden("__RequestVerificationToken", requestToken));
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"email\">邮箱</label>");
        body.Append(CultureInfo.InvariantCulture, $"<input id=\"email\" name=\"email\" type=\"email\" autocomplete=\"email\" required autofocus value=\"{E(email)}\" />");
        body.Append("</div>");
        body.Append("<button class=\"btn-primary\" type=\"submit\">发送重置链接</button>");
        body.Append("</form>");

        body.Append("<p class=\"hint\"><a href=\"/account/login\">返回登录</a></p>");

        return Layout("重置密码 - AuthHub", body.ToString());
    }

    /// <summary>
    /// 重置密码页：从邮件链接带过来的 email + token 以隐藏字段提交，
    /// 用户只需输入新密码。token 不展示给用户（防肩窥），但仍随表单回传。
    /// </summary>
    public static string ResetPasswordPage(
        string email,
        string token,
        string requestToken,
        string? error = null)
    {
        var body = new StringBuilder();

        body.Append("<h1>设置新密码</h1>");
        body.Append("<p class=\"subtitle\">为你的账号设置一个新密码。</p>");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"alert\" role=\"alert\">{E(error)}</div>");
        }

        body.Append("<form method=\"post\" action=\"/account/reset-password\">");
        body.Append(Hidden("__RequestVerificationToken", requestToken));
        body.Append(Hidden("email", email));
        body.Append(Hidden("token", token));
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"newPassword\">新密码</label>");
        body.Append("<input id=\"newPassword\" name=\"newPassword\" type=\"password\" autocomplete=\"new-password\" required autofocus />");
        body.Append("</div>");
        body.Append("<p class=\"hint\">密码至少 8 位，需包含大小写字母、数字和特殊字符。</p>");
        body.Append("<button class=\"btn-primary\" type=\"submit\">确认重置</button>");
        body.Append("</form>");

        return Layout("设置新密码 - AuthHub", body.ToString());
    }

    /// <summary>
    /// 重发邮箱确认页：输入邮箱后发送确认链接；无论是否存在/已确认都同一提示，防枚举。
    /// </summary>
    public static string ResendConfirmationPage(string requestToken, string? error = null, string? email = null)
    {
        var body = new StringBuilder();

        body.Append("<h1>重发确认邮件</h1>");
        body.Append("<p class=\"subtitle\">输入注册邮箱，我们将重新发送确认链接。</p>");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"alert\" role=\"alert\">{E(error)}</div>");
        }

        body.Append("<form method=\"post\" action=\"/account/resend-confirmation\">");
        body.Append(Hidden("__RequestVerificationToken", requestToken));
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"email\">邮箱</label>");
        body.Append(CultureInfo.InvariantCulture, $"<input id=\"email\" name=\"email\" type=\"email\" autocomplete=\"email\" required autofocus value=\"{E(email)}\" />");
        body.Append("</div>");
        body.Append("<button class=\"btn-primary\" type=\"submit\">发送确认邮件</button>");
        body.Append("</form>");

        body.Append("<p class=\"hint\"><a href=\"/account/login\">返回登录</a></p>");

        return Layout("重发确认邮件 - AuthHub", body.ToString());
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

    /// <summary>Development 短信收件箱页：最近发出的验证码（仅本机调试）。</summary>
    public static string SmsDevInboxPage(IReadOnlyList<(string Phone, string Message, string SentAt)> messages)
    {
        var body = new StringBuilder();
        body.Append("<h1>短信收件箱</h1>");
        body.Append("<p class=\"subtitle\">Development 专用。MFA 等短信不会真发，最新在上，刷新本页即可。</p>");

        if (messages.Count == 0)
        {
            body.Append("<p class=\"hint\">暂无短信。触发一次 Phone 通道 MFA 后再刷新。</p>");
        }
        else
        {
            body.Append("<ul class=\"dev-sms-list\">");
            foreach (var (phone, message, sentAt) in messages)
            {
                body.Append("<li>");
                body.Append(CultureInfo.InvariantCulture, $"<div class=\"dev-sms-meta\">{E(sentAt)} · {E(phone)}</div>");
                body.Append(CultureInfo.InvariantCulture, $"<pre class=\"dev-sms-body\">{E(message)}</pre>");
                body.Append("</li>");
            }
            body.Append("</ul>");
        }

        body.Append("<p class=\"hint\"><a href=\"/account/login\">返回登录</a></p>");
        return Layout("短信收件箱 - AuthHub", body.ToString(), wide: true);
    }

    // ------------------------------------------------------------------ 内部辅助

    // scriptSrc：可选的同源脚本（CSP script-src 'self'，只允许 wwwroot 下的文件）。
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
                 <span class="brand-mark" aria-hidden="true">AH</span>
                 <div class="brand-text">
                   <span class="brand-name">AuthHub</span>
                   <span class="brand-tagline">统一认证授权中心</span>
                 </div>
               </div>
               {body}
               <div class="footer">OpenID Connect 1.0 · OAuth 2.0</div>
             </main>
             {(scriptSrc is null ? string.Empty : $"<script src=\"{E(scriptSrc)}\"></script>")}
           </body>
           </html>
           """;

    private static string Hidden(string name, string? value)
        => $"<input type=\"hidden\" name=\"{E(name)}\" value=\"{E(value)}\" />";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
