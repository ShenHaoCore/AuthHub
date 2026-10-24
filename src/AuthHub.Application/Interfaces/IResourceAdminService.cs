using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Resources;

namespace AuthHub.Application.Interfaces;

/// <summary>
/// 资源标识白名单的管理(增/删/查/启停)。
///
/// <para>白名单是库表(RegisteredResources),这里的每次变更**即时生效**:
/// OpenIddict 资源校验每请求现查该表,无需重启。</para>
/// </summary>
public interface IResourceAdminService
{
    Task<IReadOnlyCollection<ResourceDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Result<ResourceDto>> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<Result<ResourceDto>> CreateAsync(CreateResourceRequest request, CancellationToken cancellationToken = default);

    Task<Result<ResourceDto>> UpdateAsync(long id, UpdateResourceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除登记。**策略**:若仍有客户端持有该资源的 <c>rsrc:</c> 权限,拒绝删除(409)——
    /// 必须先在客户端侧收回权限,保证"资源下线"永远是显式的两步操作,
    /// 不会出现"删了资源,一批客户端静默失效"的意外。
    /// </summary>
    Task<Result> DeleteAsync(long id, CancellationToken cancellationToken = default);
}
