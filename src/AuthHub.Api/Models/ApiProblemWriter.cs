using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Models;

/// <summary>
/// 错误响应的唯一出口（RFC 7807 ProblemDetails）。
///
/// 之所以单独抽出来，是因为它有两个调用点，且必须形状完全一致：
///   1) 控制器：<see cref="ApiControllerBase.Problem"/> 把 Application 层的 Result 翻译成 HTTP；
///   2) 认证 / 授权失败：<c>AuthHubAuthorizationResultHandler</c> 在认证方案循环结束后写响应。
///
/// 另一个关键点是 <see cref="WriteAsync"/> **直接写响应流**，不经过 MVC 的内容协商。
/// 原因是控制器上的 <c>[Produces("application/json")]</c> 会在结果过滤器里清空并覆盖
/// <c>ObjectResult.ContentTypes</c>，把 RFC 7807 要求的 <c>application/problem+json</c>
/// 悄悄改回 <c>application/json</c>，破坏下游按 Content-Type 分支处理的逻辑。
/// </summary>
public static class ApiProblemWriter
{
    /// <summary>RFC 7807 规定的媒体类型（显式带 charset，避免中文 Detail 被按 latin-1 解析）。</summary>
    public const string ContentType = "application/problem+json; charset=utf-8";

    /// <summary>构造一个带 <c>code</c> / <c>traceId</c>（以及可选 <c>errors</c>）的 ProblemDetails。</summary>
    public static ProblemDetails Create(
        HttpContext context,
        int statusCode,
        string title,
        string detail,
        string code,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        // code：稳定的机器可读错误码（控制器层用 Result.Error.Code，认证层用 Unauthorized / Forbidden）
        problem.Extensions["code"] = code;

        // traceId：与 Serilog 日志关联，线上凭它定位具体那一次请求
        problem.Extensions["traceId"] = context.TraceIdentifier;

        if (errors is { Count: > 0 })
        {
            problem.Extensions["errors"] = errors;
        }

        return problem;
    }

    /// <summary>把 ProblemDetails 写入响应（同时设置状态码）。</summary>
    public static Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        // options 传 null 会走 DI 里的 JsonOptions，与其余接口保持同样的命名策略（camelCase）
        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: ContentType);
    }
}
