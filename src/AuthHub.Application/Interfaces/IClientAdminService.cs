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

    /// <summary>
    /// 禁用 / 恢复一个**自动注册**（CIMD）的客户端。
    ///
    /// <para>为什么不是删除：删除之后同一个 <c>client_id</c> 下次请求会立刻被重新注册回来 ——
    /// 删除对自动注册的客户端没有"封禁"效果。禁用标记会保留在记录上，刷新元数据也不会清掉它。</para>
    ///
    /// <para>手工登记的客户端不支持这个操作：它们本来就只有管理员能创建，
    /// 要收回权限直接改配置或删除更直接。</para>
    /// </summary>
    Task<Result> SetAutoRegisteredDisabledAsync(
        string clientId,
        bool disabled,
        CancellationToken cancellationToken = default);
}
