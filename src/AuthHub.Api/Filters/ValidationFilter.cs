using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AuthHub.Api.Filters;

/// <summary>
/// FluentValidation 自动校验过滤器。
///
/// 为什么不用 [ApiController] 的模型校验：DataAnnotations 只能表达简单规则，
/// 而本项目把规则集中写在 Application 层的 FluentValidation 验证器里（含跨字段规则，
/// 例如“public 客户端不能带密钥”“非 localhost 必须 https”）。
/// 该过滤器按参数类型解析 IValidator&lt;T&gt;，命中即校验，失败直接返回 400。
/// </summary>
public sealed class ValidationFilter : IAsyncActionFilter
{
    private readonly IServiceProvider _services;

    public ValidationFilter(IServiceProvider services)
    {
        _services = services;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (_services.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            var validationResult = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);
            if (validationResult.IsValid)
            {
                continue;
            }

            var errors = validationResult.Errors
                .GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? "request" : e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

            var problem = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "请求参数有误",
                Detail = "请求校验未通过，请检查 errors 中的具体字段。",
                Instance = context.HttpContext.Request.Path
            };

            problem.Extensions["code"] = "ValidationFailed";
            problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

            context.Result = new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json" }
            };

            return;
        }

        await next();
    }
}
