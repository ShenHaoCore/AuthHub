using AuthHub.Api.Models;
using AuthHub.Application.DTOs.Scopes;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>
/// Scope / API 资源管理（需要 scopes.manage 权限）。
/// 按客户端批量撤销令牌的接口在 <see cref="TokensController"/>（POST /api/tokens/revoke）。
/// </summary>
[Tags("OAuth Scope")]
[Route("api/scopes")]
[Authorize(Policy = AuthHubConstants.Policies.ScopesManage)]
[Produces("application/json")]
public class ScopesController : ApiControllerBase
{
    private readonly IScopeAdminService _scopes;

    public ScopesController(IScopeAdminService scopes)
    {
        _scopes = scopes;
    }

    [EndpointSummary("查询全部 Scope")]
    [EndpointDescription("返回 Scope 及其可访问的资源列表。")]
    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _scopes.GetAllAsync(HttpContext.RequestAborted));

    [EndpointSummary("查询 Scope 详情")]
    [EndpointDescription("按 Scope 名返回单个 Scope。")]
    [HttpGet("{name}")]
    public Task<IActionResult> Get(string name)
        => ExecuteAsync(() => _scopes.GetAsync(name, HttpContext.RequestAborted));

    [EndpointSummary("创建 Scope")]
    [EndpointDescription("Scope 名约定为「资源:动作」形式，如 api:read。")]
    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateScopeRequest request)
        => ExecuteAsync(() => _scopes.CreateAsync(request, HttpContext.RequestAborted));

    [EndpointSummary("更新 Scope")]
    [EndpointDescription("Scope 名不可修改，只能改显示名、说明与资源。")]
    [HttpPut("{name}")]
    public Task<IActionResult> Update(string name, [FromBody] UpdateScopeRequest request)
        => ExecuteAsync(() => _scopes.UpdateAsync(name, request, HttpContext.RequestAborted));

    [EndpointSummary("删除 Scope")]
    [EndpointDescription("按 Scope 名删除。")]
    [HttpDelete("{name}")]
    public Task<IActionResult> Delete(string name)
        => ExecuteAsync(() => _scopes.DeleteAsync(name, HttpContext.RequestAborted));
}
