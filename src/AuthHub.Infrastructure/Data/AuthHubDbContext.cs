using AuthHub.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AuthHub.Infrastructure.Data;

/// <summary>
/// 统一数据上下文：Identity 表 + OpenIddict 表 + AuthHub 自定义表。
/// Identity 与 OpenIddict 共用同一个 DbContext，客户端/令牌变更可与业务数据放在同一个事务里。
/// </summary>
public class AuthHubDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
{
    /// <summary>把 UTC 时刻存成 UtcTicks（long）。见 <see cref="ApplySqliteDateTimeOffsetWorkaround"/>。</summary>
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToTicks =
        new(offset => offset.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableDateTimeOffsetToTicks =
        new(
            offset => offset == null ? null : offset.Value.UtcTicks,
            ticks => ticks == null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero));

    public AuthHubDbContext(DbContextOptions<AuthHubDbContext> options)
        : base(options)
    {
    }

    /// <summary>审计日志。</summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>「角色 → 权限」归属的运行时覆盖（后台界面写入）。见 <see cref="RolePermissionOverride"/>。</summary>
    public DbSet<RolePermissionOverride> RolePermissionOverrides => Set<RolePermissionOverride>();

    /// <summary>已登记的 RFC 8707 资源标识（资源白名单）。见 <see cref="RegisteredResource"/>。</summary>
    public DbSet<RegisteredResource> RegisteredResources => Set<RegisteredResource>();

    /// <summary>CIMD 元数据到期重取的跨实例租约。见 <see cref="CimdRefreshLease"/>。</summary>
    public DbSet<CimdRefreshLease> CimdRefreshLeases => Set<CimdRefreshLease>();

    // OpenIddict 的实体（Application / Authorization / Scope / Token / Device）无需声明 DbSet：
    // ModelBuilder.UseOpenIddict() 会把它们加入模型。

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // 注册 OpenIddict 实体映射（表名默认 OpenIddictApplications 等，与 Identity 表不冲突）
        builder.UseOpenIddict();

        // 应用本程序集内所有 IEntityTypeConfiguration
        builder.ApplyConfigurationsFromAssembly(typeof(AuthHubDbContext).Assembly);

        // 统一 Identity 表名前缀，便于与 OpenIddict 表区分
        builder.Entity<ApplicationUser>().ToTable("AspNetUsers");
        builder.Entity<ApplicationRole>().ToTable("AspNetRoles");

        ApplySqliteDateTimeOffsetWorkaround(builder);
    }

    /// <summary>SQLite 专用：把自定义实体的 <see cref="DateTimeOffset"/> 属性改为按 UtcTicks（long）存储。</summary>
    /// <remarks>
    /// - SQLite 提供程序翻译不了 DateTimeOffset 的比较（排序可以），时间筛选会抛
    ///   "The LINQ expression ... could not be translated"；SQL Server 不受影响，仍用原生 datetimeoffset。
    /// - 新增带 DateTimeOffset 的实体必须加进下面的数组：漏了只会在 SQLite 上炸、SQL Server 正常，
    ///   故障只在开发机与集成测试里复现。
    /// - 不碰 OpenIddict 的实体：它对 SQLite 有自己的时间存储策略，不该被这里覆盖。
    /// - 副作用：SQLite 既有数据读不出来（TEXT 变 INTEGER），开发/测试库删掉重建即可。
    /// </remarks>
    private void ApplySqliteDateTimeOffsetWorkaround(ModelBuilder builder)
    {
        // 判据用 Database.ProviderName：模型本就按提供程序分别构建并缓存，同进程混用两种提供程序也不互相污染。
        if (!IsSqlite())
        {
            return;
        }

        foreach (var clrType in new[]
                 {
                     typeof(ApplicationUser), typeof(ApplicationRole), typeof(AuditLog), typeof(RolePermissionOverride),
                     typeof(RegisteredResource), typeof(CimdRefreshLease)
                 })
        {
            foreach (var property in builder.Entity(clrType).Metadata.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(DateTimeOffsetToTicks);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(NullableDateTimeOffsetToTicks);
                }
            }
        }
    }

    /// <summary>当前是否运行在 SQLite 上。经 <c>Database.ProviderName</c> 判断（EF 官方支持），不引 Sqlite 包少一层依赖。</summary>
    private bool IsSqlite()
        => Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
}
