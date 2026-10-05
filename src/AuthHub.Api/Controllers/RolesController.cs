using AuthHub.Api.Models;
using AuthHub.Application.DTOs.Roles;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>角色与权限目录（需要 roles.manage 权限）。</summary>
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

    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _roles.GetAllAsync(HttpContext.RequestAborted));

    /// <summary>权限目录 + 角色权限映射，供管理界面渲染。</summary>
    [HttpGet("permissions")]
    public IActionResult GetPermissionCatalog()
        => Ok(new
        {
            permissions = _roles.GetPermissionCatalog(),
            rolePermissions = _roles.GetRolePermissionMap()
        });

    [HttpGet("{name}")]
    public Task<IActionResult> GetByName(string name)
        => ExecuteAsync(() => _roles.GetByNameAsync(name, HttpContext.RequestAborted));

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateRoleRequest request)
        => ExecuteAsync(() => _roles.CreateAsync(request, HttpContext.RequestAborted));

    [HttpPut("{name}")]
    public Task<IActionResult> Update(string name, [FromBody] UpdateRoleRequest request)
        => ExecuteAsync(() => _roles.UpdateAsync(name, request, HttpContext.RequestAborted));

    [HttpDelete("{name}")]
    public Task<IActionResult> Delete(string name)
        => ExecuteAsync(() => _roles.DeleteAsync(name, HttpContext.RequestAborted));
}
