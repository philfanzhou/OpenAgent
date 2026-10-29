using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class LlmInteractionEntityConfiguration : IEntityTypeConfiguration<LlmInteractionEntity>
{
    public void Configure(EntityTypeBuilder<LlmInteractionEntity> entity)
    {
        entity.ToTable("llm_interaction_logs");
        entity.HasKey(item => item.InteractionId);
        entity.Property(item => item.InteractionId).HasMaxLength(64);
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.UserId).HasMaxLength(256);
        entity.Property(item => item.ConversationId).HasMaxLength(64);
        entity.Property(item => item.TraceId).HasMaxLength(256);
        entity.Property(item => item.AgentId).HasMaxLength(256);
        entity.Property(item => item.Provider).HasMaxLength(256);
        entity.Property(item => item.ApiFormat).HasMaxLength(32);
        entity.Property(item => item.ModelId).HasMaxLength(256);
        entity.Property(item => item.RequestJson).HasColumnType("jsonb");
        entity.Property(item => item.ResponseJson).HasColumnType("jsonb");
        entity.Property(item => item.ErrorMessage).HasMaxLength(1024);
        entity.HasIndex(item => new { item.TenantId, item.ConversationId, item.StartedAt });
        entity.HasIndex(item => item.TraceId);
        entity.HasOne<ConversationEntity>().WithMany().HasForeignKey(item => item.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
