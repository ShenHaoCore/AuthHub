using AuthHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthHub.Infrastructure.Data.Configurations;

/// <summary>
/// <see cref="CimdRefreshLease"/> 的映射。
/// ClientId 是业务键（同一个客户端同时只允许一个刷新者），因此直接做主键。
/// </summary>
public sealed class CimdRefreshLeaseConfiguration : IEntityTypeConfiguration<CimdRefreshLease>
{
    public void Configure(EntityTypeBuilder<CimdRefreshLease> builder)
    {
        builder.ToTable("CimdRefreshLeases");

        builder.HasKey(lease => lease.ClientId);

        builder.Property(lease => lease.ClientId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(lease => lease.HolderToken)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(lease => lease.ExpiresAt)
            .IsRequired();
    }
}
