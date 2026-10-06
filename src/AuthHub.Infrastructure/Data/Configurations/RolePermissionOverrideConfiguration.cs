using AuthHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthHub.Infrastructure.Data.Configurations;

public sealed class RolePermissionOverrideConfiguration : IEntityTypeConfiguration<RolePermissionOverride>
{
    public void Configure(EntityTypeBuilder<RolePermissionOverride> builder)
    {
        builder.ToTable("RolePermissionOverrides");

        builder.HasKey(x => x.RoleName);

        builder.Property(x => x.RoleName).HasMaxLength(256);

        // Permissions 是 string[]，刻意**不**加值转换器：
        // EF Core 8 起会把基元集合（primitive collection）自动映射成 JSON 文本列
        // （SQL Server 为 nvarchar(max)、SQLite 为 TEXT），这正是我们要的"一行一角色的完整快照"。
        // 加转换器反而会跟基元集合的映射规则打架。
        builder.Property(x => x.Permissions).IsRequired();

        builder.Property(x => x.UpdatedBy).HasMaxLength(256);

        builder.Property(x => x.UpdatedAt).IsRequired();
    }
}
