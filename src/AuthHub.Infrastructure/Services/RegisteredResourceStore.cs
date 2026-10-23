using AuthHub.Application.Interfaces;
using AuthHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// <see cref="IRegisteredResourceStore"/> 的 EF 实现。
///
/// <para>刻意**不做缓存**:白名单的默认拒绝红线要求"变更即生效",缓存意味着要设计失效机制
/// （跨实例的 TTL/主动失效,见 issue #23 的风险条款）。单次查询走唯一索引,每请求一次的
/// 开销很小;将来真要加缓存,必须在管理 API 的变更路径上做主动失效,而不是靠 TTL。</para>
/// </summary>
public sealed class RegisteredResourceStore(AuthHubDbContext dbContext) : IRegisteredResourceStore
{
    public async Task<IReadOnlyCollection<string>> GetEnabledResourcesAsync(CancellationToken cancellationToken = default)
        => await dbContext.RegisteredResources
            .AsNoTracking()
            .Where(resource => resource.Enabled)
            .Select(resource => resource.Resource)
            .ToArrayAsync(cancellationToken);
}
