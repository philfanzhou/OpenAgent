using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class ConversationFileReferenceEntityConfiguration : IEntityTypeConfiguration<ConversationFileReferenceEntity>
{
    public void Configure(EntityTypeBuilder<ConversationFileReferenceEntity> entity)
    {
        entity.ToTable("conversation_file_references");
        entity.HasKey(item => new { item.ConversationId, item.FileId });
        entity.Property(item => item.ConversationId).HasMaxLength(64);
        entity.Property(item => item.FileId).HasMaxLength(64);
        entity.HasIndex(item => item.FileId);
        entity.HasOne<ConversationEntity>().WithMany().HasForeignKey(item => item.ConversationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<FileAssetEntity>().WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}
