using AuthHub.Api.Extensions;
using AuthHub.Api.Models;
using AuthHub.Application.DTOs.Users;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>用户管理（需要 users.manage 权限）。</summary>
[Tags("用户管理")]
[Route("api/users")]
[Authorize(Policy = AuthHubConstants.Policies.UsersManage)]
[Produces("application/json")]
public class UsersController : ApiControllerBase
{
    private readonly IUserAdminService _users;
    private readonly ITokenAdminService _tokens;

    public UsersController(IUserAdminService users, ITokenAdminService tokens)
    {
        _users = users;
        _tokens = tokens;
    }

    /// <summary>分页查询用户。</summary>
    [EndpointSummary("分页查询用户")]
    [EndpointDescription("支持关键字、角色、启用状态筛选，分页返回。")]
    [HttpGet]
    public async Task<IActionResult> Query([FromQuery] UserQuery query)
        => Ok(await _users.QueryAsync(query, HttpContext.RequestAborted));

    [EndpointSummary("查询用户详情")]
    [EndpointDescription("按用户 Id 返回单个用户。")]
    [HttpGet("{id}")]
    public Task<IActionResult> GetById(string id)
        => ExecuteAsync(() => _users.GetByIdAsync(id, HttpContext.RequestAborted));

    [EndpointSummary("创建用户")]
    [EndpointDescription("用户名与邮箱在系统内唯一。")]
    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateUserRequest request)
        => ExecuteAsync(() => _users.CreateAsync(request, HttpContext.RequestAborted));

    [EndpointSummary("更新用户资料")]
    [EndpointDescription("按用户 Id 更新资料，只提交需要变更的字段。")]
    [HttpPut("{id}")]
    public Task<IActionResult> Update(string id, [FromBody] UpdateUserRequest request)
        => ExecuteAsync(() => _users.UpdateAsync(id, request, HttpContext.RequestAborted));

    [EndpointSummary("删除用户")]
    [EndpointDescription("删除用户，并清理其令牌。")]
    [HttpDelete("{id}")]
    public Task<IActionResult> Delete(string id)
        => ExecuteAsync(() => _users.DeleteAsync(id, HttpContext.RequestAborted));

    /// <summary>全量覆盖该用户的角色。</summary>
    [EndpointSummary("覆盖用户角色")]
    [EndpointDescription("传入完整角色列表，未包含的角色会被移除。")]
    [HttpPut("{id}/roles")]
    public Task<IActionResult> AssignRoles(string id, [FromBody] AssignRolesRequest request)
        => ExecuteAsync(() => _users.AssignRolesAsync(id, request, HttpContext.RequestAborted));

    /// <summary>锁定 / 解锁账号。</summary>
    [EndpointSummary("锁定或解锁账号")]
    [EndpointDescription("locked 为 true 表示锁定，false 表示解锁。")]
    [HttpPost("{id}/lockout")]
    public Task<IActionResult> SetLockout(string id, [FromQuery] bool locked = true)
        => ExecuteAsync(() => _users.SetLockoutAsync(id, locked, HttpContext.RequestAborted));

    /// <summary>
    /// 强制下线：撤销该用户的全部令牌与授权。
    /// 被撤销的令牌即使尚未过期也会立刻失效（令牌存储 + EnableTokenEntryValidation）。
    /// </summary>
    [EndpointSummary("强制用户下线")]
    [EndpointDescription("撤销该用户全部令牌与授权，未过期令牌立即失效。")]
    [HttpPost("{id}/revoke-tokens")]
    public Task<IActionResult> RevokeTokens(string id)
        => ExecuteAsync(() => _tokens.RevokeBySubjectAsync(id, HttpContext.RequestAborted));
}
