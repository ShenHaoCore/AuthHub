using AuthHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 数据访问层的注册。
///
/// 本项目让 Identity、OpenIddict 与自定义业务表共用**同一个 DbContext**：
/// 它们之间本来就有外键关系（授权记录指向应用、用户角色指向用户），拆成多个上下文
/// 只会让联表查询与事务变得别扭。因此这里只注册一个 AuthHubDbContext。
///
/// 提供程序由 <c>Database:Provider</c> 决定：SQLite 用于开发与集成测试（无需外部服务），
/// 其余走 SQL Server（带 3 次重试，覆盖 Azure SQL 式的瞬时故障）。
/// </summary>
internal static class PersistenceExtensions
{
    public static IServiceCollection AddAuthHubPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? DatabaseProviders.SqlServer;
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("缺少连接字符串 ConnectionStrings:DefaultConnection。");

        services.AddDbContext<AuthHubDbContext>(options =>
        {
            if (DatabaseProviders.IsSqlite(provider))
            {
                options.UseSqlite(connectionString);
            }
            else
            {
                options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(maxRetryCount: 3));
            }

            // 注册 OpenIddict 的实体映射（Application / Authorization / Scope / Token / Device）
            options.UseOpenIddict();
        });

        return services;
    }
}
