using AuthHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthHub.Infrastructure.Data.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasMaxLength(64);

        builder.Property(x => x.Action).HasMaxLength(200).IsRequired();

        builder.Property(x => x.UserId).HasMaxLength(450);

        builder.Property(x => x.UserName).HasMaxLength(256);

        builder.Property(x => x.ClientId).HasMaxLength(100);

        builder.Property(x => x.IpAddress).HasMaxLength(45);

        builder.Property(x => x.UserAgent).HasMaxLength(512);

        builder.Property(x => x.Details).HasMaxLength(2000);

        builder.Property(x => x.CreatedAt).IsRequired();

        // 审计查询的典型路径：按动作、按用户、按时间倒序
        builder.HasIndex(x => new { x.Action, x.CreatedAt });

        builder.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}
