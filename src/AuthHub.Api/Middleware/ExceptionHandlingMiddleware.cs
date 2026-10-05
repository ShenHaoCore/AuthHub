using AuthHub.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Middleware;

/// <summary>
/// 全局异常兜底。
/// 关键点：异常详情只写日志、不回传给客户端（生产环境），
/// 客户端拿到的是统一的 ProblemDetails + traceId，凭 traceId 到日志里定位。
///
/// 响应体走 <see cref="ApiProblemWriter"/>，与控制器 / 认证 / 校验失败同一条出口 ——
/// 这保证了 500 的 Content-Type 也是 <c>application/problem+json</c>，
/// 而不是默认的 <c>application/json</c>。
/// </summary>
public sealed class ExceptionHandlingMiddleware : IMiddleware
{
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "未处理异常：{Method} {Path}（traceId={TraceId}）",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);

            if (context.Response.HasStarted)
            {
                // 响应已经开始写出，无法再改写，交给上层终止连接
                throw;
            }

            context.Response.Clear();

            var problem = ApiProblemWriter.Create(
                context,
                StatusCodes.Status500InternalServerError,
                "服务内部错误",
                _environment.IsDevelopment()
                    ? exception.Message
                    : "服务器处理请求时发生异常，请稍后重试；如持续出现请联系管理员并提供 traceId。",
                "InternalError");

            if (_environment.IsDevelopment())
            {
                problem.Extensions["exception"] = exception.ToString();
            }

            await ApiProblemWriter.WriteAsync(context, problem);
        }
    }
}
