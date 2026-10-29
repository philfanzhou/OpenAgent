using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class MessageFileReferenceEntityConfiguration : IEntityTypeConfiguration<MessageFileReferenceEntity>
{
    public void Configure(EntityTypeBuilder<MessageFileReferenceEntity> entity)
    {
        entity.ToTable("message_file_references");
        entity.HasKey(item => new { item.MessageId, item.FileId });
        entity.Property(item => item.MessageId).HasMaxLength(64);
        entity.Property(item => item.FileId).HasMaxLength(64);
        entity.HasIndex(item => item.FileId);
        entity.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(item => item.MessageId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<FileAssetEntity>().WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}
