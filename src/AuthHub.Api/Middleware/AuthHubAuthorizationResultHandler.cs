using AuthHub.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AuthHub.Api.Middleware;

/// <summary>
/// <c>/api/*</c> 上认证 / 授权失败的统一出口（RFC 7807 ProblemDetails）。
/// </summary>
/// <remarks>
/// - 存在原因：管理类策略同时挂两个认证方案（Identity 会话 Cookie 与 OpenIddict Bearer），
///   默认处理器会依次对每个方案 Challenge / Forbid，Cookie 方案的事件先写出响应体的话，
///   排在后面的方案再设状态码会抛 "The status code cannot be set, the response has already started"。
/// - 方案：各方案只补自己的响应头（如 WWW-Authenticate），谁都不写响应体
///   （Cookie 事件的 /api 分支见 Program.cs / IdentityExtensions），循环结束后由这里统一写出。
/// - 非 /api 路径（浏览器页面、<c>/connect/*</c>）原样交给默认处理器，保持 302 跳登录页等 HTML 流程行为。
/// </remarks>
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
