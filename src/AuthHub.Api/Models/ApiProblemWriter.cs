using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AuthHub.Api.Models;

/// <summary>
/// 错误响应的唯一出口（RFC 7807 ProblemDetails）。
///
/// 之所以单独抽出来，是因为它有四个调用点，且必须形状完全一致：
///   1) 控制器：<see cref="ApiControllerBase.Problem"/> 把 Application 层的 Result 翻译成 HTTP；
///   2) 认证 / 授权失败：<c>AuthHubAuthorizationResultHandler</c> 在认证方案循环结束后写响应；
///   3) 请求体绑定失败（<c>[ApiController]</c> 的自动 400）：<see cref="InvalidModelStateResponse"/>；
///   4) FluentValidation 校验失败：<c>ValidationFilter</c>（它把错误塞进 ModelState 后复用同一个工厂）；
///   5) 未处理异常：<c>ExceptionHandlingMiddleware</c>。
///
/// **为什么这几处曾经各写各的**（一段真实的历史，别再退回去）：
///   后三处原先分别手工拼 ProblemDetails、各自设置 Content-Type。结果是同一个服务里
///   有**两套 400 契约** —— 绑定失败那套由框架产出（英文标题、<b>没有 code 字段</b>、
///   traceId 是 W3C traceparent），业务校验失败那套才有 code/traceId。
///   下游按 <c>code</c> 分支处理时会静默漏掉一半 400。
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
    /// 把 MVC 的 <see cref="ModelStateDictionary"/> 翻译成统一的 400 响应。
    ///
    /// 两个调用点共用它：
    ///   - <c>[ApiController]</c> 的 <c>InvalidModelStateResponseFactory</c>（请求体绑定失败）；
    ///   - <c>ValidationFilter</c>（FluentValidation 规则不通过，错误先写进 ModelState 再走这里）。
    /// 这样"绑定失败"和"业务规则失败"对外是同一个契约。
    ///
    /// 边界：字段级文案直接沿用 ModelState 里的原文。绑定失败时框架给的是英文
    /// （"The X field is required."），本项目未引入本地化资源，因此不做翻译 ——
    /// 要中文化需要成体系地配 DataAnnotations 资源，不属于"统一出口"这件事。
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

        // 必须显式传**运行时类型**：WriteAsJsonAsync 的泛型重载按声明类型（ProblemDetails）
        // 做序列化，ValidationProblemDetails 上的 Errors 会被静默丢掉 ——
        // 表现就是"400 里没有 errors 字段"，而状态码与 Content-Type 全都正常，很难发现。
        //
        // options 传 null 会走 DI 里的 JsonOptions，与其余接口保持同样的命名策略（camelCase）
        return context.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            options: null,
            contentType: ContentType);
    }
}
