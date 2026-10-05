using System.Globalization;
using System.Net;
using System.Text;
using AuthHub.Application.DTOs.Consents;

namespace AuthHub.Api.Pages;

/// <summary>
/// 服务端渲染的极简 HTML 页面（登录 / 两阶段验证 / 同意授权 / 提示）。
///
/// 为什么不用 Razor：本项目的定位是 OIDC 服务端 + 管理 API，
/// 浏览器页面只有这几个“协议流程必需”的页面。
/// 用纯字符串产出 HTML 可以保持 Api 项目零视图依赖、零前端构建步骤，
/// 同时便于集成测试直接断言页面内容。
///
/// 安全上做了三件事：
///   1) 所有动态内容一律 HTML 编码，避免反射型 XSS；
///   2) 表单只提交到同源路径（配合 CSP 的 form-action 'self'）；
///   3) 表单内嵌防伪令牌（__RequestVerificationToken）。
/// </summary>
public static class HtmlPages
{
    private const string Styles = """
        :root {
          color-scheme: light;
          --bg: #f5f6f8;
          --card: #ffffff;
          --border: #e3e6ea;
          --text: #1f2328;
          --muted: #656d76;
          --primary: #2563eb;
          --primary-hover: #1d4ed8;
          --danger: #d1242f;
          --danger-bg: #fff5f5;
          --danger-border: #ffd7d7;
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
          background: var(--bg); color: var(--text);
          font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Microsoft YaHei", Roboto, Helvetica, Arial, sans-serif;
          font-size: 14px; line-height: 1.6;
        }
        .card {
          width: 100%; max-width: 420px; margin: 24px; padding: 32px;
          background: var(--card); border: 1px solid var(--border); border-radius: 12px;
          box-shadow: 0 1px 2px rgba(31,35,40,.04), 0 8px 24px rgba(31,35,40,.06);
        }
        .card.wide { max-width: 480px; }
        .brand { display: flex; align-items: center; gap: 8px; margin-bottom: 20px; }
        .brand-mark {
          width: 28px; height: 28px; border-radius: 8px; background: var(--primary);
          color: #fff; font-weight: 700; display: flex; align-items: center; justify-content: center; font-size: 14px;
        }
        .brand-name { font-weight: 600; font-size: 15px; }
        h1 { font-size: 20px; margin: 0 0 6px; font-weight: 600; }
        .subtitle { color: var(--muted); margin: 0 0 20px; }
        label { display: block; font-weight: 500; margin-bottom: 6px; }
        input[type=text], input[type=password], input[type=email] {
          width: 100%; padding: 9px 12px; border: 1px solid var(--border); border-radius: 8px;
          font-size: 14px; font-family: inherit; background: #fff; color: var(--text);
        }
        input:focus { outline: 2px solid rgba(37,99,235,.35); outline-offset: 0; border-color: var(--primary); }
        .field { margin-bottom: 14px; }
        .checkbox { display: flex; align-items: center; gap: 8px; color: var(--muted); margin-bottom: 18px; }
        .checkbox input { margin: 0; }
        button {
          width: 100%; padding: 10px 16px; border: 0; border-radius: 8px; cursor: pointer;
          font-size: 14px; font-weight: 600; font-family: inherit;
        }
        .btn-primary { background: var(--primary); color: #fff; }
        .btn-primary:hover { background: var(--primary-hover); }
        .btn-secondary { background: #fff; color: var(--text); border: 1px solid var(--border); }
        .btn-secondary:hover { background: #f3f4f6; }
        .actions { display: flex; gap: 10px; margin-top: 22px; }
        .actions button { flex: 1; }
        .alert {
          padding: 10px 12px; border-radius: 8px; margin-bottom: 18px;
          background: var(--danger-bg); border: 1px solid var(--danger-border); color: var(--danger);
        }
        .scope-list { list-style: none; padding: 0; margin: 0 0 4px; }
        .scope-list li { padding: 12px 0; border-bottom: 1px solid var(--border); }
        .scope-list li:last-child { border-bottom: 0; }
        .scope-name { font-weight: 600; }
        .scope-code { color: var(--muted); font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
        .client-name { font-weight: 600; color: var(--primary); }
        .hint { color: var(--muted); font-size: 12px; margin-top: 18px; }
        .footer { margin-top: 22px; text-align: center; color: var(--muted); font-size: 12px; }
        a { color: var(--primary); }
        """;

    public static string LoginPage(string returnUrl, string requestToken, string? error = null, string? userName = null)
    {
        var body = new StringBuilder();

        body.Append("<h1>登录 AuthHub</h1>");
        body.Append("<p class=\"subtitle\">使用你的统一账号继续访问应用</p>");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"alert\" role=\"alert\">{E(error)}</div>");
        }

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
        body.Append("<label for=\"rememberMe\" style=\"margin:0;font-weight:400\">在此设备上保持登录</label>");
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
        body.Append("<label for=\"rememberMachine\" style=\"margin:0;font-weight:400\">在此设备上不再要求验证码</label>");
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

    private static string Layout(string title, string body, bool wide = false)
        => $"""
           <!DOCTYPE html>
           <html lang="zh-CN">
           <head>
             <meta charset="utf-8" />
             <meta name="viewport" content="width=device-width, initial-scale=1" />
             <meta name="robots" content="noindex, nofollow" />
             <title>{E(title)}</title>
             <style>{Styles}</style>
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
           </body>
           </html>
           """;

    private static string Hidden(string name, string? value)
        => $"<input type=\"hidden\" name=\"{E(name)}\" value=\"{E(value)}\" />";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
