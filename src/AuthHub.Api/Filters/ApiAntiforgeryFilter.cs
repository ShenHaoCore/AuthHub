using AuthHub.Api.Models;
using AuthHub.Api.Extensions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AuthHub.Api.Filters;

/// <summary>
/// 双认证方案端点的防伪过滤：只有当请求确实以<strong>会话 Cookie</strong> 完成认证时
/// 才要求防伪令牌，失败返回 400 ProblemDetails。
///
/// <para>为什么不是 [ValidateAntiForgeryToken]：管理 /api 同时服务浏览器（Cookie）与
/// 脚本（Bearer，见 AuthorizationPolicyExtensions 的策略定义）—— Bearer 客户端没有
/// ambient 凭证，CSRF 不适用，强迫它们走浏览器令牌舞蹈只是摩擦没有收益。方案感知的
/// 过滤把校验精确落在"有 Cookie 可被滥用"的那类请求上。</para>
///
/// <para>匿名请求不在此处理：401 由认证层与 AuthHubAuthorizationResultHandler 统一写出。
/// 纯 Cookie 认证的控制器（AccountApiController）直接用源内 MVC 特性，不经本过滤器。</para>
/// </summary>
public sealed class ApiAntiforgeryFilter : IAsyncAuthorizationFilter
{
    private readonly IAntiforgery _antiforgery;

    public ApiAntiforgeryFilter(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            return;
        }

        var identity = context.HttpContext.User.Identity;
        if (identity?.IsAuthenticated != true ||
            !string.Equals(identity.AuthenticationType, AuthHubSchemes.Cookie, StringComparison.Ordinal))
        {
            // 匿名 / Bearer：无 ambient Cookie 可被跨站滥用，401 与业务逻辑照旧
            return;
        }

        try
        {
            await _antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new ObjectResult(ApiProblemWriter.Create(
                context.HttpContext,
                StatusCodes.Status400BadRequest,
                title: "防伪令牌缺失或不匹配。",
                detail: "Cookie 认证的变更请求必须携带 X-CSRF-TOKEN 头（GET /api/antiforgery/token 获取）。",
                code: "antiforgery_invalid"))
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }
    }
}
