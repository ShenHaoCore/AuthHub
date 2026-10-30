using AuthHub.Domain.Entities;
using AuthHub.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AuthHub.Infrastructure.Data.Seed;

/// <summary>资源白名单的「配置 → 库」幂等种子。</summary>
/// <remarks>
/// - 独立于 AuthHub:Seeding:Enabled：资源白名单是安全配置、没有演示成分，老部署的 <c>AuthHub:Resources</c>
///   必须升级后原样生效，不能因「生产没开 Seeding」把白名单清空（所有 resource 请求会变成 invalid_target）。
///   因此它只要求表已建好（跟在迁移之后）。
/// - 只做加法：已存在的资源跳过（管理员在库里的显示名/说明/启用状态不动），从配置删掉的项也不从库里移除
///   （下线走管理 API 或 Enabled=false）—— 配置是部署产物，库状态含运行期人工操作，以库为准更安全。
/// </remarks>
public static class ResourceSeeder
{
    public static async Task SeedAsync(
        AuthHubDbContext dbContext,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // 启动期 fail-fast：配置里的非法 URI 让进程起不来（与库表化之前的行为一致）。
        var configured = AuthHubResourceRegistry.LoadValidated(configuration);

        if (configured.Length == 0)
        {
            return;
        }

        var existing = (await dbContext.RegisteredResources
                .Select(resource => resource.Resource)
                .ToArrayAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;

        foreach (var resource in configured)
        {
            // 配置资源先经 NormalizeResource 规范化再登记（见其注释），
            // 否则存量部署会出现"同一资源换写法就 invalid_target"。
            var normalized = AuthHubResourceRegistry.NormalizeResource(resource)
                ?? throw new InvalidOperationException(
                    $"{AuthHubResourceRegistry.SectionName} 中的 \"{resource}\" 不是可登记的资源标识:" +
                    "RFC 8707 要求资源标识是绝对 URI 且不含 fragment。");

            if (existing.Contains(normalized))
            {
                continue;
            }

            dbContext.RegisteredResources.Add(new RegisteredResource
            {
                Resource = normalized,
                Enabled = true
            });

            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("已从配置 {Section} 播种 {Count} 个资源标识入库。", AuthHubResourceRegistry.SectionName, added);
        }
    }
}
