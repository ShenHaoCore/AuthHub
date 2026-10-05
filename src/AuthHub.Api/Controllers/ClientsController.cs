using AuthHub.Api.Models;
using AuthHub.Application.DTOs.Clients;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>
/// OAuth 客户端管理（需要 clients.manage 权限）。
///
/// 安全约定：
///   - 客户端密钥只存哈希，接口永不回读明文；
///   - 创建 / 轮换时明文密钥只在响应里返回一次，请立即转存到密钥管理服务。
/// </summary>
[Tags("OAuth 客户端")]
[Route("api/clients")]
[Authorize(Policy = AuthHubConstants.Policies.ClientsManage)]
[Produces("application/json")]
public class ClientsController : ApiControllerBase
{
    private readonly IClientAdminService _clients;

    public ClientsController(IClientAdminService clients)
    {
        _clients = clients;
    }

    [EndpointSummary("分页查询客户端")]
    [EndpointDescription("支持按 client_id 或显示名搜索。")]
    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
        => Ok(await _clients.QueryAsync(page, pageSize, search, HttpContext.RequestAborted));

    [EndpointSummary("查询客户端详情")]
    [EndpointDescription("按 client_id 返回单个客户端。")]
    [HttpGet("{clientId}")]
    public Task<IActionResult> Get(string clientId)
        => ExecuteAsync(() => _clients.GetAsync(clientId, HttpContext.RequestAborted));

    /// <summary>创建客户端。未提供密钥时自动生成，明文密钥仅在本次响应中返回。</summary>
    [EndpointSummary("创建客户端")]
    [EndpointDescription("未提供密钥时自动生成；明文密钥仅在本次响应中返回一次。")]
    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateClientRequest request)
        => ExecuteAsync(() => _clients.CreateAsync(request, HttpContext.RequestAborted));

    [EndpointSummary("更新客户端")]
    [EndpointDescription("可调整显示名、授权类型、Scope 与重定向地址。")]
    [HttpPut("{clientId}")]
    public Task<IActionResult> Update(string clientId, [FromBody] UpdateClientRequest request)
        => ExecuteAsync(() => _clients.UpdateAsync(clientId, request, HttpContext.RequestAborted));

    /// <summary>轮换客户端密钥，旧密钥立即失效。</summary>
    [EndpointSummary("轮换客户端密钥")]
    [EndpointDescription("生成新密钥并让旧密钥立即失效，明文密钥只返回一次。")]
    [HttpPost("{clientId}/rotate-secret")]
    public Task<IActionResult> RotateSecret(string clientId)
        => ExecuteAsync(() => _clients.RotateSecretAsync(clientId, HttpContext.RequestAborted));

    [EndpointSummary("删除客户端")]
    [EndpointDescription("同时清理该客户端的令牌与授权记录。")]
    [HttpDelete("{clientId}")]
    public Task<IActionResult> Delete(string clientId)
        => ExecuteAsync(() => _clients.DeleteAsync(clientId, HttpContext.RequestAborted));
}
