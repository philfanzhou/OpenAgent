using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class FileShareLinkEntityConfiguration : IEntityTypeConfiguration<FileShareLinkEntity>
{
    public void Configure(EntityTypeBuilder<FileShareLinkEntity> entity)
    {
        entity.ToTable("file_share_links");
        entity.HasKey(item => item.ShareIdHash);
        entity.Property(item => item.ShareIdHash).HasMaxLength(64);
        entity.Property(item => item.FileId).HasMaxLength(64);
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.OwnerUserId).HasMaxLength(256);
        entity.Property(item => item.ObjectKey).HasMaxLength(2048);
        entity.Property(item => item.FileName).HasMaxLength(1024);
        entity.Property(item => item.MediaType).HasMaxLength(256);
        entity.HasIndex(item => new { item.TenantId, item.OwnerUserId, item.CreatedAt });
        entity.HasOne<FileAssetEntity>().WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}
