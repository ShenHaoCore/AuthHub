using AuthHub.Application.Interfaces;
using AuthHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthHub.Infrastructure.Services;

/// <summary>
/// <see cref="IRolePermissionOverrideStore"/> 的 EF 实现。
///
/// 做成**单例**（消费方 <c>LayeredRolePermissionMap</c> 也是单例，且处在登录热路径上），
/// 所以这里不能直接注入 scoped 的 <see cref="AuthHubDbContext"/> ——
/// 每次读取时开一个短命的 scope 取上下文，读完即弃。
///
/// 表里只有个位数的行，一次整表读取是刻意的：各数据库默认排序规则对大小写的处理不一致
/// （SQLite 主键区分大小写、SQL Server 通常不区分），走 SQL 等值查询会让同一个角色名
/// 在两种提供程序下取到不同结果；在内存里按不区分大小写比对则没有这个歧义。
/// </summary>
public sealed class EfRolePermissionOverrideStore : IRolePermissionOverrideStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public EfRolePermissionOverrideStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public IReadOnlyDictionary<string, string[]> Load()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthHubDbContext>();

        var rows = dbContext.RolePermissionOverrides
            .AsNoTracking()
            .Select(x => new { x.RoleName, x.Permissions })
            .ToList();

        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // 用索引器而不是 ToDictionary：理论上可能存在只差大小写的两行，ToDictionary 会直接抛
            result[row.RoleName] = row.Permissions ?? Array.Empty<string>();
        }

        return result;
    }
}
