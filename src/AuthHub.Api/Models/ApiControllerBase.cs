using AuthHub.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Models;

/// <summary>
/// 控制器基类：把 Application 层的 <see cref="Result"/> 统一翻译成 HTTP 响应。
///
/// 错误一律使用 RFC 7807 ProblemDetails（Content-Type: application/problem+json），
/// 并额外带上 <c>code</c> 与 <c>traceId</c>：
///   - code：稳定的机器可读错误码，便于下游做分支处理；
///   - traceId：与日志关联，便于排查。
/// 校验失败时附带 <c>errors</c>，结构与 ASP.NET Core 内建的 ValidationProblemDetails 一致。
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>执行一个返回 Result 的业务操作并映射为 HTTP 响应。</summary>
    protected async Task<IActionResult> ExecuteAsync(Func<Task<Result>> action)
    {
        var result = await action();
        return result.IsSuccess ? NoContent() : Problem(result);
    }

    protected async Task<IActionResult> ExecuteAsync<T>(Func<Task<Result<T>>> action)
    {
        var result = await action();
        return result.IsSuccess ? Ok(result.Value) : Problem(result);
    }

    /// <summary>把失败结果转换为 ProblemDetails。</summary>
    protected IActionResult Problem(Result result)
    {
        var (statusCode, title) = result.Error.Type switch
        {
            ErrorType.Validation => (StatusCodes.Status400BadRequest, "请求参数有误"),
            ErrorType.Unauthorized => (StatusCodes.Status401Unauthorized, "未认证"),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, "无权限"),
            ErrorType.NotFound => (StatusCodes.Status404NotFound, "资源不存在"),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, "资源冲突"),
            ErrorType.LockedOut => (StatusCodes.Status423Locked, "账号已锁定"),
            _ => (StatusCodes.Status500InternalServerError, "服务内部错误")
        };

        var problem = ApiProblemWriter.Create(
            HttpContext,
            statusCode,
            title,
            result.Error.Message,
            result.Error.Code,
            result.Error.ValidationErrors);

        return new ProblemDetailsResult(problem);
    }
}
