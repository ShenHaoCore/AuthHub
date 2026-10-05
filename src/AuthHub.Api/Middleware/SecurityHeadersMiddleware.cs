namespace AuthHub.Api.Middleware;

/// <summary>
/// 安全响应头。
/// 由服务端统一注入，避免各控制器遗漏；HSTS 只在 HTTPS 请求上返回。
/// </summary>
public sealed class SecurityHeadersMiddleware : IMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "script-src 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'";

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["X-XSS-Protection"] = "1; mode=block";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            // 登录 / 同意页是服务端渲染的 HTML，需要 CSP；对 JSON 响应同样无害
            headers["Content-Security-Policy"] = ContentSecurityPolicy;

            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
