using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class ConversationMessageEntityConfiguration : IEntityTypeConfiguration<ConversationMessageEntity>
{
    public void Configure(EntityTypeBuilder<ConversationMessageEntity> entity)
    {
        entity.ToTable("conversation_messages");
        entity.HasKey(item => item.MessageId);
        entity.Property(item => item.MessageId).HasMaxLength(64);
        entity.Property(item => item.ConversationId).HasMaxLength(64);
        entity.Property(item => item.Role).HasMaxLength(32);
        entity.Property(item => item.ToolCallId).HasMaxLength(256);
        entity.Property(item => item.ToolName).HasMaxLength(256);
        entity.Property(item => item.IdempotencyKey).HasMaxLength(256);
        entity.Property(item => item.ModelId).HasMaxLength(256);
        entity.Property(item => item.TraceId).HasMaxLength(256);
        entity.Property(item => item.MetadataJson).HasColumnType("jsonb");
        entity.HasIndex(item => new { item.ConversationId, item.Sequence }).IsUnique();
        entity.HasOne<ConversationEntity>().WithMany().HasForeignKey(item => item.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
