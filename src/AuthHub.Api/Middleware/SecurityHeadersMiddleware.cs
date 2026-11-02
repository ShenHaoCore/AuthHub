namespace AuthHub.Api.Middleware;

/// <summary>
/// 安全响应头。
/// 由服务端统一注入，避免各控制器遗漏；HSTS 只在 HTTPS 请求上返回。
/// </summary>
public sealed class SecurityHeadersMiddleware : IMiddleware
{
    /// <summary>
    /// API 文档 UI（Scalar）的路径前缀。该前缀下的 CSP 需要放宽 <c>script-src</c>，
    /// 理由见 <see cref="ApiDocsContentSecurityPolicy"/>。
    /// </summary>
    private const string ApiDocsPathPrefix = "/scalar";

    /// <summary>
    /// 两套策略共用的指令（除 <c>script-src</c> 外完全一致）。收敛到一处，
    /// 避免日后只在其中一份上补指令、另一份悄悄退化。
    /// </summary>
    private const string SharedDirectives =
        "default-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "form-action 'self'; " +
        "frame-src 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'";

    /// <summary>
    /// 基准策略：只允许同源脚本，内联脚本与 <c>on*</c> 事件属性一律拒绝。
    /// 登录 / 同意 / 错误提示页、管理后台、全部 API 响应都走这一套。
    /// </summary>
    private const string ContentSecurityPolicy = SharedDirectives + "; script-src 'self'";

    /// <summary>
    /// API 文档 UI（Scalar）专用策略：<c>script-src</c> 额外放行内联脚本。
    /// </summary>
    /// <remarks>
    /// - 必须放宽：Scalar 页面由包内嵌模板产出，含一段内联 script（承载文档源与 OAuth2 预填参数），
    ///   被 CSP 拦掉后 initialize() 不执行，页面返回 200 但 #app 容器永远空白。
    /// - 不用 nonce：HTML 由 Scalar 内嵌模板生成，.NET 集成没有注入口子
    ///   （官方 nonce 方案只覆盖 Next.js / Express 等 JS 系集成）。
    /// - 可接受：该响应是不含任何请求可控数据的固定模板，不存在可注入内容；
    ///   放宽范围被 <see cref="ApiDocsPathPrefix"/> 限制在这一条路径上。
    /// - 集成测试对"基准策略不含 unsafe-inline"与"文档策略含 unsafe-inline"都有断言，防止放宽被抄到全局。
    /// </remarks>
    private const string ApiDocsContentSecurityPolicy = SharedDirectives + "; script-src 'self' 'unsafe-inline'";

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // 策略按路径选：文档 UI 用放宽版，其余一律严格版。
        // 在进入管道前算好，避免把路径判断塞进响应回调。
        var contentSecurityPolicy = context.Request.Path.StartsWithSegments(ApiDocsPathPrefix)
            ? ApiDocsContentSecurityPolicy
            : ContentSecurityPolicy;

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            // 显式关闭旧浏览器内置的 XSS 审计器（OWASP / Keycloak / Duende 的现行建议）：
            // "1; mode=block" 存在可被利用的旁路，现代浏览器已移除该功能；真正的防线是上面的 CSP 与全量 HTML 编码。
            headers["X-XSS-Protection"] = "0";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            // 登录 / 同意页是服务端渲染的 HTML，需要 CSP；对 JSON 响应同样无害
            headers["Content-Security-Policy"] = contentSecurityPolicy;

            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
