using AuthHub.Api.Models;
using AuthHub.Application.DTOs.Resources;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>
/// 资源标识白名单管理（需要 resources.manage 权限）。
///
/// <para>这里登记的是 RFC 8707 资源标识（绝对 URI），用于 MCP / 资源服务器的
/// 受众（aud）白名单。校验以库为准、变更即时生效 —— 不需要重启；
/// <c>AuthHub:Resources</c> 配置只是启动期的幂等种子。</para>
/// </summary>
[Tags("OAuth Resources")]
[Route("api/resources")]
[Authorize(Policy = AuthHubConstants.Policies.ResourcesManage)]
[Produces("application/json")]
public class ResourcesController : ApiControllerBase
{
    private readonly IResourceAdminService _resources;

    public ResourcesController(IResourceAdminService resources)
    {
        _resources = resources;
    }

    [EndpointSummary("查询全部已登记资源")]
    [EndpointDescription("返回资源标识白名单（含停用的登记，Enabled=false 等同未登记）。")]
    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _resources.GetAllAsync(HttpContext.RequestAborted));

    [EndpointSummary("查询资源登记详情")]
    [EndpointDescription("按数据库 Id 返回单条资源登记。")]
    [HttpGet("{id:long}")]
    public Task<IActionResult> Get(long id)
        => ExecuteAsync(() => _resources.GetAsync(id, HttpContext.RequestAborted));

    [EndpointSummary("登记资源")]
    [EndpointDescription("登记一个 RFC 8707 资源标识（绝对 URI，无 fragment），登记后立即生效。")]
    [HttpPost]
    // codeql[cs/web/missing-token-validation] 本控制器整体走 Bearer JWT（策略 ResourcesManage），
    // 不存在 Cookie 会话，CSRF 攻击面不成立；反伪造令牌只用于后台表单（CookieSession）。
    public Task<IActionResult> Create([FromBody] CreateResourceRequest request)
        => ExecuteAsync(() => _resources.CreateAsync(request, HttpContext.RequestAborted));

    [EndpointSummary("更新资源登记")]
    [EndpointDescription("资源标识不可修改，只能改显示名、说明与启停（Enabled=false 等同未登记）。")]
    [HttpPut("{id:long}")]
    public Task<IActionResult> Update(long id, [FromBody] UpdateResourceRequest request)
        => ExecuteAsync(() => _resources.UpdateAsync(id, request, HttpContext.RequestAborted));

    [EndpointSummary("删除资源登记")]
    [EndpointDescription("仍有客户端持有该资源 rsrc: 权限时返回 409，需先收回客户端授权再删除。")]
    [HttpDelete("{id:long}")]
    public Task<IActionResult> Delete(long id)
        => ExecuteAsync(() => _resources.DeleteAsync(id, HttpContext.RequestAborted));
}
