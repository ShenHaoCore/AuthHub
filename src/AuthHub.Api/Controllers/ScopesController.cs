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

    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _scopes.GetAllAsync(HttpContext.RequestAborted));

    [HttpGet("{name}")]
    public Task<IActionResult> Get(string name)
        => ExecuteAsync(() => _scopes.GetAsync(name, HttpContext.RequestAborted));

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateScopeRequest request)
        => ExecuteAsync(() => _scopes.CreateAsync(request, HttpContext.RequestAborted));

    [HttpPut("{name}")]
    public Task<IActionResult> Update(string name, [FromBody] UpdateScopeRequest request)
        => ExecuteAsync(() => _scopes.UpdateAsync(name, request, HttpContext.RequestAborted));

    [HttpDelete("{name}")]
    public Task<IActionResult> Delete(string name)
        => ExecuteAsync(() => _scopes.DeleteAsync(name, HttpContext.RequestAborted));
}
