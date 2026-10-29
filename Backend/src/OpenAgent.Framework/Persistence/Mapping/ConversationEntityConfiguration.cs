using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class ConversationEntityConfiguration : IEntityTypeConfiguration<ConversationEntity>
{
    public void Configure(EntityTypeBuilder<ConversationEntity> entity)
    {
        entity.ToTable("conversations");
        entity.HasKey(item => item.ConversationId);
        entity.Property(item => item.ConversationId).HasMaxLength(64);
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.UserId).HasMaxLength(256);
        entity.Property(item => item.AgentId).HasMaxLength(256);
        entity.Property(item => item.TraceId).HasMaxLength(256);
        entity.Property(item => item.Title).HasMaxLength(512);
        entity.Property(item => item.ContextSummariesJson).HasColumnType("jsonb");
        entity.Property(item => item.Version).IsConcurrencyToken();
        entity.HasIndex(item => new { item.TenantId, item.LastMessageAt });
        entity.HasIndex(item => new { item.TenantId, item.IsDeletedByUser, item.LastMessageAt });
    }
}
