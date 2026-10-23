using AuthHub.Domain.Entities;
using AuthHub.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AuthHub.Infrastructure.Data.Seed;

/// <summary>
/// 资源白名单的「配置 → 库」幂等种子。
///
/// <para><b>为什么独立于 AuthHub:Seeding:Enabled</b>：演示种子里有公开口令的账号/客户端,
/// 需要生产二次确认（见 <see cref="AuthHubSeeder.Decide"/>）；而资源白名单是安全配置,
/// 没有任何演示成分 —— 老部署的 <c>AuthHub:Resources</c> 必须在升级后原样生效,
/// 不能因为「生产没开 Seeding」就把白名单清空（那等于把所有 resource 请求变成 invalid_target）。
/// 因此它只要求表已建好（跟在迁移之后）。</para>
///
/// <para>幂等与方向：已存在的资源一律跳过（管理员在库里的显示名/说明/启用状态不动）;
/// 从配置里删掉的项也**不**从库里移除 —— 配置只做加法种子,下线资源请走管理 API
/// （或把 Enabled 置 false）。这个方向是刻意选的:配置文件通常是部署产物,
/// 而库里的状态可能包含运行期的人工操作,以库为准更安全。</para>
/// </summary>
public static class ResourceSeeder
{
    public static async Task SeedAsync(
        AuthHubDbContext dbContext,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // 启动期 fail-fast:配置里的非法 URI 让进程起不来（与库表化之前的行为一致,
        // 那时 RegisterResources 在启动时就把异常抛了）
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
            // 与旧实现的行为等价:配置资源先经 new Uri() 规范化再登记
            // （见 NormalizeResource 的注释),否则存量部署会出现"同一资源换写法就 invalid_target"
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
