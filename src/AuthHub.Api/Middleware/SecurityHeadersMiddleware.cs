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
    /// 两套策略共用的指令。除 <c>script-src</c> 外完全一致 ——
    /// 刻意把公共部分收敛到一处，避免日后只在其中一份上补指令、另一份悄悄退化。
    /// <c>frame-src</c> 只放行企业微信扫码域：登录页「扫码登录」页签以 iframe 直嵌官方
    /// 二维码面板（自嵌 iframe 而非官方 wwLogin.js，符合本站「无 CDN 引用」约束）。
    /// </summary>
    private const string SharedDirectives =
        "default-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "form-action 'self'; " +
        "frame-src https://login.work.weixin.qq.com; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'";

    /// <summary>
    /// 基准策略：只允许同源脚本，内联脚本与 <c>on*</c> 事件属性一律拒绝。
    /// 登录 / 同意 / 错误提示页、管理后台、全部 API 响应都走这一套。
    /// </summary>
    private const string ContentSecurityPolicy = SharedDirectives + "; script-src 'self'";

    /// <summary>
    /// API 文档 UI（Scalar）专用策略：<c>script-src</c> 额外放行内联脚本。
    ///
    /// <para><b>为什么必须放宽</b>：Scalar 的页面由包内嵌模板产出，其中包含一段内联的
    /// <c>&lt;script type="module"&gt;</c>，承载文档源地址与 OAuth2 预填参数
    /// （形如 <c>initialize('/scalar/v1', false, { authentication: ..., sources: [...] })</c>）。
    /// 它被拦掉后 <c>initialize()</c> 不会执行，<c>#app</c> 容器永远是空的 ——
    /// 页面返回 200、HTML 结构完整、资源全部可取，唯独浏览器里一片空白，
    /// 只看响应码完全发现不了。</para>
    ///
    /// <para><b>为什么不用 nonce</b>：nonce 是更优解，但需要把 nonce 写进那两个 script 标签，
    /// 而 HTML 由 Scalar 的内嵌模板生成，.NET 集成没有提供注入口子
    /// （Scalar 官方文档里的 nonce 方案只覆盖 Next.js / Express 等 JS 系集成）。
    /// 拿不到 nonce 就只能放行 inline。</para>
    ///
    /// <para><b>为什么可以接受</b>：该响应是不含任何请求可控数据的固定模板
    /// （标题、favicon、文档源、OAuth2 参数全部是代码常量），不存在可注入的内容，
    /// 内联脚本的执行体完全由服务端代码决定；且放宽范围被
    /// <see cref="ApiDocsPathPrefix"/> 限制在这一条路径上，业务页面与 API 仍走基准策略。</para>
    ///
    /// <para>集成测试对"基准策略不含 <c>unsafe-inline</c>"与"文档策略含 <c>unsafe-inline</c>"
    /// 都有断言，防止这份放宽被顺手抄到全局。</para>
    /// </summary>
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
            headers["X-XSS-Protection"] = "1; mode=block";
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
