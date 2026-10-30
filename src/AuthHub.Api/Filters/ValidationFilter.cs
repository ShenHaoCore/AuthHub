using AuthHub.Api.Models;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AuthHub.Api.Filters;

/// <summary>
/// FluentValidation 自动校验过滤器：按参数类型解析 <c>IValidator&lt;T&gt;</c>，命中即校验，失败直接返回 400。
/// 不用 [ApiController] 的模型校验：规则集中写在 Application 层的验证器里（含跨字段规则），
/// DataAnnotations 只能表达简单规则。
/// </summary>
/// <remarks>
/// - 响应形状不在这里拼：错误写进 <c>ModelState</c> 后交给 <see cref="ApiProblemWriter.InvalidModelStateResponse"/>，
///   与 [ApiController] 的绑定失败共用同一出口。曾在这里自拼 <c>ObjectResult</c>，
///   被控制器上的 <c>[Produces("application/json")]</c> 在结果过滤器阶段覆盖 Content-Type 后，
///   校验失败实际发出的是 <c>application/json</c>；换成直接写响应流的
///   <see cref="ProblemDetailsResult"/> 才绕得开内容协商。
/// </remarks>
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

            var validationResult = await validator.ValidateAsync(
                new ValidationContext<object>(argument),
                context.HttpContext.RequestAborted);

            if (validationResult.IsValid)
            {
                continue;
            }

            foreach (var error in validationResult.Errors)
            {
                // 空属性名（RuleFor(x => x) 这种整对象规则）归到 "request" 键，与框架约定一致
                var key = string.IsNullOrEmpty(error.PropertyName)
                    ? ApiProblemWriter.RequestErrorKey
                    : error.PropertyName;

                context.ModelState.AddModelError(key, error.ErrorMessage);
            }

            context.Result = ApiProblemWriter.InvalidModelStateResponse(context);
            return;
        }

        await next();
    }
}
