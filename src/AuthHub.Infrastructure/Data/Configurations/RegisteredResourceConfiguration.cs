using AuthHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthHub.Infrastructure.Data.Configurations;

/// <summary>
/// <see cref="RegisteredResource"/> 的映射。
/// Resource 列有唯一索引:资源标识就是这张表的业务键,重复登记属于错误而非覆盖
/// (要改显示名/说明走 Update,要重新启用走 Enabled 开关)。
/// </summary>
public sealed class RegisteredResourceConfiguration : IEntityTypeConfiguration<RegisteredResource>
{
    public void Configure(EntityTypeBuilder<RegisteredResource> builder)
    {
        builder.ToTable("RegisteredResources");

        builder.HasKey(resource => resource.Id);

        builder.Property(resource => resource.Resource)
            .HasMaxLength(450)
            .IsRequired();

        builder.HasIndex(resource => resource.Resource)
            .IsUnique();

        builder.Property(resource => resource.DisplayName)
            .HasMaxLength(200);

        builder.Property(resource => resource.Description)
            .HasMaxLength(1000);
    }
}
