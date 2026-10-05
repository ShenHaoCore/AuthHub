using AuthHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthHub.Infrastructure.Data.Configurations;

public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.DisplayName).HasMaxLength(128).IsRequired();

        builder.Property(u => u.LastLoginIp).HasMaxLength(45);

        builder.Property(u => u.IsActive).HasDefaultValue(true);

        builder.Property(u => u.CreatedAt).IsRequired();

        // 管理端按“是否启用 + 创建时间”列表查询
        builder.HasIndex(u => new { u.IsActive, u.CreatedAt });
    }
}
