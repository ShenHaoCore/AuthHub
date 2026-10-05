using AuthHub.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AuthHub.Api.Middleware;

/// <summary>
/// <c>/api/*</c> 上认证 / 授权失败的统一出口。
///
/// 为什么需要它（这是一个真实的坑）：
///   管理类策略同时挂了两个认证方案（Identity 会话 Cookie 与 OpenIddict Bearer），
///   策略校验失败时，<c>AuthorizationMiddlewareResultHandler</c> 会**依次**对每个方案
///   调用 Challenge / Forbid。而 Cookie 方案的事件默认会写出响应体（跳转或 JSON），
///   响应一旦开始就无法再改状态码 —— 排在后面的 OpenIddict Validation 处理器再去设置
///   状态码时会抛：
///       InvalidOperationException: The status code cannot be set, the response has already started.
///   结果：只要策略里方案的先后顺序不同，同一类失败行为就会一个正常、一个 500。
///   这也解释了为什么 <c>(Bearer, Cookie)</c> 顺序的策略"看起来没问题"—— 恰好是
///   不写响应体的 Bearer 排在前面、写响应体的 Cookie 排在最后而已（纯属顺序巧合）。
///
/// 解决办法：
///   让每个方案只负责补齐自己的响应头（例如 OpenIddict 的 WWW-Authenticate），
///   谁都不写响应体（见 Program.cs 里 Cookie 事件的 /api 分支），
///   等方案循环结束后，由这里统一写出 RFC 7807 ProblemDetails。
///
/// 非 <c>/api</c> 路径（浏览器页面、<c>/connect/*</c>）原样交给默认处理器，
/// 保持 302 跳登录页这类 HTML 流程行为不变。
/// </summary>
public sealed class AuthHubAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>API 路径前缀。注册时按此判断是否需要 JSON 语义的失败响应。</summary>
    public const string ApiPathPrefix = "/api";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var needsApiError = context.Request.Path.StartsWithSegments(ApiPathPrefix)
            && (authorizeResult.Challenged || authorizeResult.Forbidden);

        if (!needsApiError)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        // 让各方案设置状态码 / WWW-Authenticate。这些方案在 /api 下都不会写响应体，
        // 因此到这里响应仍未开始，状态码可以安全改写。
        //
        // 注意：HttpContext 上的 ChallengeAsync/ForbidAsync 扩展方法只接受单个方案，
        // 多方案的依次调用是 AuthenticationService 的内部行为，这里显式照做一遍。
        foreach (var scheme in policy.AuthenticationSchemes)
        {
            if (context.Response.HasStarted)
            {
                break;
            }

            if (authorizeResult.Challenged)
            {
                await context.ChallengeAsync(scheme);
            }
            else
            {
                await context.ForbidAsync(scheme);
            }
        }

        if (context.Response.HasStarted)
        {
            // 兜底：万一某个方案已经写出了响应体，就不再改写，避免二次异常把 401/403 变成 500
            return;
        }

        var (statusCode, title, detail, code) = authorizeResult.Challenged
            ? (StatusCodes.Status401Unauthorized,
               "未认证",
               "当前请求没有携带有效的登录会话或访问令牌。请先登录，或改用 Bearer 访问令牌调用。",
               "Unauthorized")
            : (StatusCodes.Status403Forbidden,
               "无权限",
               "当前身份没有访问该资源所需的权限。",
               "Forbidden");

        await ApiProblemWriter.WriteAsync(
            context,
            ApiProblemWriter.Create(context, statusCode, title, detail, code));
    }
}
