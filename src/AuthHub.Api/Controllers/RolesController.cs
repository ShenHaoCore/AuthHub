using AuthHub.Api.Models;
using AuthHub.Application.DTOs.Roles;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>角色与权限目录（需要 roles.manage 权限）。</summary>
[Tags("角色与权限")]
[Route("api/roles")]
[Authorize(Policy = AuthHubConstants.Policies.RolesManage)]
[Produces("application/json")]
public class RolesController : ApiControllerBase
{
    private readonly IRoleAdminService _roles;

    public RolesController(IRoleAdminService roles)
    {
        _roles = roles;
    }

    [EndpointSummary("查询全部角色")]
    [EndpointDescription("返回角色及其权限列表。")]
    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _roles.GetAllAsync(HttpContext.RequestAborted));

    /// <summary>权限目录 + 角色权限映射，供管理界面渲染。</summary>
    [EndpointSummary("查询权限目录")]
    [EndpointDescription("返回全部权限点，以及内置角色与权限的固定映射。")]
    [HttpGet("permissions")]
    public IActionResult GetPermissionCatalog()
        => Ok(new
        {
            permissions = _roles.GetPermissionCatalog(),
            rolePermissions = _roles.GetRolePermissionMap()
        });

    [EndpointSummary("查询角色详情")]
    [EndpointDescription("按角色名返回单个角色。")]
    [HttpGet("{name}")]
    public Task<IActionResult> GetByName(string name)
        => ExecuteAsync(() => _roles.GetByNameAsync(name, HttpContext.RequestAborted));

    [EndpointSummary("创建角色")]
    [EndpointDescription("角色名不可与现有角色重复。")]
    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateRoleRequest request)
        => ExecuteAsync(() => _roles.CreateAsync(request, HttpContext.RequestAborted));

    [EndpointSummary("更新角色")]
    [EndpointDescription("内置角色的权限由代码固定，不可在此修改。")]
    [HttpPut("{name}")]
    public Task<IActionResult> Update(string name, [FromBody] UpdateRoleRequest request)
        => ExecuteAsync(() => _roles.UpdateAsync(name, request, HttpContext.RequestAborted));

    [EndpointSummary("删除角色")]
    [EndpointDescription("内置角色不可删除。")]
    [HttpDelete("{name}")]
    public Task<IActionResult> Delete(string name)
        => ExecuteAsync(() => _roles.DeleteAsync(name, HttpContext.RequestAborted));
}
