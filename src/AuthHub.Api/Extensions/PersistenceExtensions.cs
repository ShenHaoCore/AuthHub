using AuthHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 数据访问层的注册。Identity、OpenIddict 与自定义业务表共用同一个 AuthHubDbContext：
/// 它们之间有外键关系（授权记录指向应用、用户角色指向用户），拆成多个上下文会让联表查询与事务变得别扭。
/// 提供程序由 <c>Database:Provider</c> 决定：SQLite 用于开发与集成测试（无需外部服务），
/// PostgreSQL 与 SQL Server 面向部署（均带 3 次重试，覆盖瞬时故障）。
/// </summary>
internal static class PersistenceExtensions
{
    public static IServiceCollection AddAuthHubPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? DatabaseProviders.SqlServer;

        // 用 IsNullOrWhiteSpace 而不是 `?? throw`：配置项存在但为空串时 GetConnectionString 返回 ""，
        // `??` 放它过去，UseSqlServer("") 要等到第一次真正连接才炸 —— 那时错误信息已看不出是少配了连接串。
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "缺少数据库连接串。三种提供方式任选一种：" +
                "环境变量 ConnectionStrings__DefaultConnection、" +
                "appsettings.{环境}.json 里的 ConnectionStrings:DefaultConnection、" +
                "或 Development 下的 User Secrets（dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"...\"）。");
        }

        services.AddDbContext<AuthHubDbContext>(options =>
        {
            if (DatabaseProviders.IsSqlite(provider))
            {
                options.UseSqlite(connectionString);
            }
            else if (DatabaseProviders.IsPostgres(provider))
            {
                // 与 SQL Server 同样的重试策略：覆盖网络抖动与 PG 连接池瞬时的 53300（too_many_connections）
                options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3));
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
