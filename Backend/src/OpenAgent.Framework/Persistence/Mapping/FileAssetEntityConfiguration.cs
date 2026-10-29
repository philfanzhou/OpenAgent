using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class FileAssetEntityConfiguration : IEntityTypeConfiguration<FileAssetEntity>
{
    public void Configure(EntityTypeBuilder<FileAssetEntity> entity)
    {
        entity.ToTable("file_assets");
        entity.HasKey(item => item.FileId);
        entity.Property(item => item.FileId).HasMaxLength(64);
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.OwnerUserId).HasMaxLength(256);
        entity.Property(item => item.FileName).HasMaxLength(1024);
        entity.Property(item => item.MediaType).HasMaxLength(256);
        entity.Property(item => item.Sha256).HasMaxLength(128);
        entity.Property(item => item.ObjectKey).HasMaxLength(2048);
        entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.CreatedAt });
    }
}
