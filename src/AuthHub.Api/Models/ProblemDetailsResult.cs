using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Models;

/// <summary>
/// 把 <see cref="ProblemDetails"/> 直接写进响应流的 <see cref="IActionResult"/>。
///
/// 为什么不用 <c>ObjectResult</c>：
///   控制器上的 <c>[Produces("application/json")]</c> 实现了 <c>IResultFilter</c>，
///   它在结果过滤器阶段会 <c>Clear()</c> 并覆盖 <c>ObjectResult.ContentTypes</c>。
///   于是"错误响应必须是 application/problem+json"这个约定会被静默破坏，
///   而且只在带 [Produces] 的控制器上出现，排查成本很高。
///   自定义 IActionResult 不参与这套内容协商，Content-Type 由我们说了算。
/// </summary>
internal sealed class ProblemDetailsResult : IActionResult
{
    private readonly ProblemDetails _problem;

    public ProblemDetailsResult(ProblemDetails problem) => _problem = problem;

    public Task ExecuteResultAsync(ActionContext context)
        => ApiProblemWriter.WriteAsync(context.HttpContext, _problem);
}
