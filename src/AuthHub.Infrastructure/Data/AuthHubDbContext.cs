using AuthHub.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AuthHub.Infrastructure.Data;

/// <summary>
/// 统一数据上下文：Identity 表 + OpenIddict 表 + AuthHub 自定义表。
/// Identity 与 OpenIddict 共用同一个 DbContext，这样客户端/令牌的变更
/// 可以和业务数据放在同一个事务里。
/// </summary>
public class AuthHubDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
{
    public AuthHubDbContext(DbContextOptions<AuthHubDbContext> options)
        : base(options)
    {
    }

    /// <summary>审计日志。</summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

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
    }
}
