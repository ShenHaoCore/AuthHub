using AuthHub.Api.Models;
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
///
/// 响应形状**不在这里拼**：错误写进 <c>ModelState</c> 后交给
/// <see cref="ApiProblemWriter.InvalidModelStateResponse"/>，与 [ApiController] 的
/// 绑定失败共用同一个出口。曾经这里自己 <c>new ObjectResult { ContentTypes = { "application/problem+json" } }</c>，
/// 而控制器上的 <c>[Produces("application/json")]</c> 会在结果过滤器阶段把这个 Content-Type 覆盖掉 ——
/// 于是校验失败实际发出去的是 <c>application/json</c>，违背 README 的「错误响应约定」。
/// 换成直接写响应流的 <see cref="ProblemDetailsResult"/> 才绕得开内容协商。
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
