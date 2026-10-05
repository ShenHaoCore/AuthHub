using AuthHub.Api.Extensions;
using AuthHub.Api.Models;
using AuthHub.Application.DTOs.Users;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>用户管理（需要 users.manage 权限）。</summary>
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
    [HttpGet]
    public async Task<IActionResult> Query([FromQuery] UserQuery query)
        => Ok(await _users.QueryAsync(query, HttpContext.RequestAborted));

    [HttpGet("{id}")]
    public Task<IActionResult> GetById(string id)
        => ExecuteAsync(() => _users.GetByIdAsync(id, HttpContext.RequestAborted));

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateUserRequest request)
        => ExecuteAsync(() => _users.CreateAsync(request, HttpContext.RequestAborted));

    [HttpPut("{id}")]
    public Task<IActionResult> Update(string id, [FromBody] UpdateUserRequest request)
        => ExecuteAsync(() => _users.UpdateAsync(id, request, HttpContext.RequestAborted));

    [HttpDelete("{id}")]
    public Task<IActionResult> Delete(string id)
        => ExecuteAsync(() => _users.DeleteAsync(id, HttpContext.RequestAborted));

    /// <summary>全量覆盖该用户的角色。</summary>
    [HttpPut("{id}/roles")]
    public Task<IActionResult> AssignRoles(string id, [FromBody] AssignRolesRequest request)
        => ExecuteAsync(() => _users.AssignRolesAsync(id, request, HttpContext.RequestAborted));

    /// <summary>锁定 / 解锁账号。</summary>
    [HttpPost("{id}/lockout")]
    public Task<IActionResult> SetLockout(string id, [FromQuery] bool locked = true)
        => ExecuteAsync(() => _users.SetLockoutAsync(id, locked, HttpContext.RequestAborted));

    /// <summary>
    /// 强制下线：撤销该用户的全部令牌与授权。
    /// 被撤销的令牌即使尚未过期也会立刻失效（令牌存储 + EnableTokenEntryValidation）。
    /// </summary>
    [HttpPost("{id}/revoke-tokens")]
    public Task<IActionResult> RevokeTokens(string id)
        => ExecuteAsync(() => _tokens.RevokeBySubjectAsync(id, HttpContext.RequestAborted));
}
