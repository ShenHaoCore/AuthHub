using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AuthHub.Api.Models;

/// <summary>错误响应的唯一出口（RFC 7807 ProblemDetails），各失败路径共用同一形状。</summary>
/// <remarks>
/// - 调用点：控制器 <see cref="ApiControllerBase.Problem"/>、认证 / 授权失败
///   （<c>AuthHubAuthorizationResultHandler</c>）、绑定失败（<see cref="InvalidModelStateResponse"/>）、
///   FluentValidation（<c>ValidationFilter</c> 复用同一工厂）、未处理异常（<c>ExceptionHandlingMiddleware</c>）。
/// - 历史：后三处曾各写各的，形成两套 400 契约（框架那套没有 code 字段），下游按 code 分支会静默漏掉一半 400。
/// - <see cref="WriteAsync"/> 直接写响应流、不经 MVC 内容协商：控制器上的 <c>[Produces("application/json")]</c>
///   会在结果过滤器里覆盖 ContentTypes，把 <c>application/problem+json</c> 改回 <c>application/json</c>。
/// </remarks>
public static class ApiProblemWriter
{
    /// <summary>RFC 7807 规定的媒体类型（显式带 charset，避免中文 Detail 被按 latin-1 解析）。</summary>
    public const string ContentType = "application/problem+json; charset=utf-8";

    /// <summary>校验类失败的稳定错误码（所有 400 都用它，见 <see cref="CreateValidation"/>）。</summary>
    public const string ValidationFailedCode = "ValidationFailed";

    /// <summary>未知错误键的兜底名：指向整个请求而非某个字段（与框架的约定一致）。</summary>
    public const string RequestErrorKey = "request";

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

    /// <summary>校验失败的统一形状。字段级错误走 <see cref="ValidationProblemDetails.Errors"/>。</summary>
    public static ValidationProblemDetails CreateValidation(
        HttpContext context,
        IReadOnlyDictionary<string, string[]> errors)
    {
        var problem = new ValidationProblemDetails(new Dictionary<string, string[]>(errors, StringComparer.Ordinal))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "请求参数有误",
            Detail = "请求校验未通过，请检查 errors 中的具体字段。",
            Instance = context.Request.Path
        };

        problem.Extensions["code"] = ValidationFailedCode;
        problem.Extensions["traceId"] = context.TraceIdentifier;

        return problem;
    }

    /// <summary>
    /// 把 MVC 的 <see cref="ModelStateDictionary"/> 翻译成统一的 400 响应，两个调用点共用：
    /// [ApiController] 的绑定失败与 ValidationFilter（错误先写进 ModelState 再走这里）。
    /// 字段级文案沿用 ModelState 原文（绑定失败时框架给英文，本地化不在"统一出口"范围内）。
    /// </summary>
    public static IActionResult InvalidModelStateResponse(ActionContext context)
    {
        var errors = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .ToDictionary(
                entry => string.IsNullOrEmpty(entry.Key) ? RequestErrorKey : entry.Key,
                entry => entry.Value!.Errors
                    .Select(error => string.IsNullOrEmpty(error.ErrorMessage)
                        ? error.Exception?.Message ?? "参数不合法。"
                        : error.ErrorMessage)
                    .ToArray(),
                StringComparer.Ordinal);

        return new ProblemDetailsResult(CreateValidation(context.HttpContext, errors));
    }

    /// <summary>把 ProblemDetails 写入响应（同时设置状态码）。</summary>
    public static Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        // 必须显式传运行时类型：泛型重载按声明类型（ProblemDetails）序列化，
        // ValidationProblemDetails 上的 Errors 会被静默丢掉（状态码与 Content-Type 全都正常，很难发现）。
        // options 传 null 走 DI 里的 JsonOptions，与其余接口保持同样的命名策略。
        return context.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            options: null,
            contentType: ContentType);
    }
}
