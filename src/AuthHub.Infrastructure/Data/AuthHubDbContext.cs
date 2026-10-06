using AuthHub.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AuthHub.Infrastructure.Data;

/// <summary>
/// 统一数据上下文：Identity 表 + OpenIddict 表 + AuthHub 自定义表。
/// Identity 与 OpenIddict 共用同一个 DbContext，这样客户端/令牌的变更
/// 可以和业务数据放在同一个事务里。
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

    // OpenIddict 的实体（Application / Authorization / Scope / Token / Device）
    // 无需手动声明 DbSet：ModelBuilder.UseOpenIddict() 会把它们加入模型。

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

    /// <summary>
    /// SQLite 专用：把自定义实体的 <see cref="DateTimeOffset"/> 属性改为按 UtcTicks（long）存储。
    ///
    /// 起因：SQLite 没有原生的 DateTimeOffset 类型，EF Core 的 SQLite 提供程序能翻译它的
    /// **排序**，却翻译不了**比较** —— <c>Where(x =&gt; x.CreatedAt &gt;= from)</c> 会直接抛
    /// "The LINQ expression ... could not be translated"。
    /// 于是"按时间区间筛选审计日志"在 SQL Server 上正常，换到 SQLite 就是 500。
    ///
    /// 换成 long（ticks）之后：
    ///   · 语义不变 —— 库里存的一直是 UTC 时刻；
    ///   · 比较与排序都能被原生翻译；
    ///   · **只对 SQLite 生效**，SQL Server 仍用原生 datetimeoffset 列类型，
    ///     生产库结构与既有迁移完全不受影响。
    ///
    /// 只处理我们自己的四张实体表（不碰 OpenIddict 的实体）：
    /// OpenIddict 对 SQLite 有自己的时间存储策略，不该被这里覆盖。
    ///
    /// **新增带 DateTimeOffset 的实体时别忘了加进下面那个数组** ——
    /// 漏了的话，在 SQLite 上对该字段做比较 / 排序会抛
    /// "The LINQ expression ... could not be translated"，而 SQL Server 上却一切正常，
    /// 于是故障只在开发机与集成测试里复现。
    ///
    /// 副作用：切换后 SQLite 上的**既有数据**读不出来（原本是 TEXT、现在是 INTEGER）。
    /// 开发/测试库删掉重建即可 —— 这也正是把 SQLite 定位为"本地开发与测试"的原因。
    /// </summary>
    private void ApplySqliteDateTimeOffsetWorkaround(ModelBuilder builder)
    {
        // 判据用 Database.ProviderName：模型本就按提供程序分别构建并缓存，
        // 同一进程里同时用两种提供程序时也不会互相污染。
        if (!IsSqlite())
        {
            return;
        }

        foreach (var clrType in new[]
                 {
                     typeof(ApplicationUser), typeof(ApplicationRole), typeof(AuditLog), typeof(RolePermissionOverride)
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

    /// <summary>
    /// 当前是否运行在 SQLite 上。
    /// <c>OnModelCreating</c> 里通过 <c>Database.ProviderName</c> 判断提供程序是 EF 官方支持的用法
    /// （模型会按提供程序分别构建与缓存），比引用 Sqlite 包里的 <c>IsSqlite()</c> 少一层依赖。
    /// </summary>
    private bool IsSqlite()
        => Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
}
