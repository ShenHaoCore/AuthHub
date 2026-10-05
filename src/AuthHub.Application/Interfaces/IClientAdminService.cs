using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Clients;

namespace AuthHub.Application.Interfaces;

/// <summary>
/// OAuth 客户端管理。实现在 Infrastructure 层，内部使用
/// <c>IOpenIddictApplicationManager</c>，因此 Application 层不引用 OpenIddict。
/// </summary>
public interface IClientAdminService
{
    Task<PagedResult<ClientDto>> QueryAsync(int page, int pageSize, string? search = null, CancellationToken cancellationToken = default);

    Task<Result<ClientDto>> GetAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>创建客户端。未提供密钥时自动生成，明文只在返回值里出现一次。</summary>
    Task<Result<ClientCreatedResponse>> CreateAsync(CreateClientRequest request, CancellationToken cancellationToken = default);

    Task<Result<ClientDto>> UpdateAsync(string clientId, UpdateClientRequest request, CancellationToken cancellationToken = default);

    /// <summary>轮换客户端密钥（旧密钥立即失效）。</summary>
    Task<Result<ClientSecretResponse>> RotateSecretAsync(string clientId, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string clientId, CancellationToken cancellationToken = default);
}
