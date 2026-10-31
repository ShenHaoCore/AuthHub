using AuthHub.Api.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;

namespace AuthHub.Api.Extensions;

/// <summary>
/// /api 的防伪约定：非匿名变更端点（POST/PUT/DELETE/PATCH）自动挂
/// <see cref="ApiAntiforgeryFilter"/>（方案感知：仅对会话 Cookie 认证的请求要求令牌），
/// 新增控制器不依赖作者记得手动标注。
///
/// <para>两类刻意豁免：匿名端点（<c>[AllowAnonymous]</c>）没有可被 CSRF 滥用的 ambient
/// 凭证，且 JSON body 天然挡掉跨站表单伪造；GET 按约定不产生副作用。
/// 源内已挂 <see cref="ValidateAntiForgeryTokenAttribute"/> 的动作（纯 Cookie 的
/// AccountApiController）会被本过滤器先做同语义校验 —— 统一 400 契约，MVC 内置过滤器
/// （Order=1000，在最后）只做冗余确认。</para>
///
/// <para>非浏览器客户端（脚本 / MCP）的接入姿势见 docs/guides/api-reference.md：
/// 先 GET /api/antiforgery/token，再把令牌放进 X-CSRF-TOKEN 头。</para>
/// </summary>
internal sealed class ApiAntiforgeryConvention : IActionModelConvention
{
    public void Apply(ActionModel action)
    {
        if (action.Filters.OfType<ApiAntiforgeryFilter>().Any())
        {
            return;
        }

        // [AllowAnonymous] 在 ActionModel 里表现为 AllowAnonymousFilter 实例
        if (action.Filters.OfType<AllowAnonymousFilter>().Any())
        {
            return;
        }

        // HTTP 方法在路由特性上（HttpPost/HttpPut/...），不是 ActionMethod.Name（那是 C# 方法名）
        var httpMethods = action.Attributes.OfType<HttpMethodAttribute>()
            .SelectMany(m => m.HttpMethods)
            .ToHashSet(StringComparer.Ordinal);

        if (!httpMethods.Overlaps(new[] { "POST", "PUT", "DELETE", "PATCH" }))
        {
            return;
        }

        // 统一走方案感知过滤器：源内挂了 [ValidateAntiForgeryToken] 的动作（AccountApiController）
        // 会经历两次校验 —— 本过滤器在前（Order=0），产出统一的 ProblemDetails 400；
        // MVC 内置过滤器（Order=1000）在后，仅在常规情况下做同语义的冗余确认。
        action.Filters.Add(new TypeFilterAttribute(typeof(ApiAntiforgeryFilter)));
    }
}
