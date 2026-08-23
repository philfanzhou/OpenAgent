using Microsoft.EntityFrameworkCore;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure;

public sealed class OpenAgentDbContext(DbContextOptions<OpenAgentDbContext> options) : DbContext(options)
{
    internal DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    internal DbSet<ConversationMessageEntity> ConversationMessages => Set<ConversationMessageEntity>();
    internal DbSet<FileAssetEntity> FileAssets => Set<FileAssetEntity>();
    internal DbSet<ConversationFileReferenceEntity> ConversationFileReferences => Set<ConversationFileReferenceEntity>();
    internal DbSet<MessageFileReferenceEntity> MessageFileReferences => Set<MessageFileReferenceEntity>();
    internal DbSet<SkillDefinitionEntity> SkillDefinitions => Set<SkillDefinitionEntity>();
    internal DbSet<HumanApprovalEntity> HumanApprovals => Set<HumanApprovalEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("openagent");

        modelBuilder.Entity<ConversationEntity>(entity =>
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
        });

        modelBuilder.Entity<ConversationMessageEntity>(entity =>
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
            entity.Property(item => item.MetadataJson).HasColumnType("jsonb");
            entity.HasIndex(item => new { item.ConversationId, item.Sequence }).IsUnique();
            entity.HasOne<ConversationEntity>().WithMany().HasForeignKey(item => item.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileAssetEntity>(entity =>
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
        });

        modelBuilder.Entity<ConversationFileReferenceEntity>(entity =>
        {
            entity.ToTable("conversation_file_references");
            entity.HasKey(item => new { item.ConversationId, item.FileId });
            entity.Property(item => item.ConversationId).HasMaxLength(64);
            entity.Property(item => item.FileId).HasMaxLength(64);
            entity.HasIndex(item => item.FileId);
            entity.HasOne<ConversationEntity>().WithMany().HasForeignKey(item => item.ConversationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<FileAssetEntity>().WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MessageFileReferenceEntity>(entity =>
        {
            entity.ToTable("message_file_references");
            entity.HasKey(item => new { item.MessageId, item.FileId });
            entity.Property(item => item.MessageId).HasMaxLength(64);
            entity.Property(item => item.FileId).HasMaxLength(64);
            entity.HasIndex(item => item.FileId);
            entity.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(item => item.MessageId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<FileAssetEntity>().WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SkillDefinitionEntity>(entity =>
        {
            entity.ToTable("skill_definitions");
            entity.HasKey(item => new { item.TenantId, item.SkillId, item.Type });
            entity.Property(item => item.TenantId).HasMaxLength(256);
            entity.Property(item => item.SkillId).HasMaxLength(256);
            entity.Property(item => item.Type).HasMaxLength(64);
            entity.Property(item => item.SourceType).HasMaxLength(64);
            entity.Property(item => item.DefinitionJson).HasColumnType("jsonb");
            entity.HasIndex(item => new { item.TenantId, item.UpdatedAt });
        });

        modelBuilder.Entity<HumanApprovalEntity>(entity =>
        {
            entity.ToTable("human_approvals");
            entity.HasKey(item => item.ApprovalId);
            entity.Property(item => item.ApprovalId).HasMaxLength(64);
            entity.Property(item => item.TenantId).HasMaxLength(256);
            entity.Property(item => item.ConversationId).HasMaxLength(64);
            entity.Property(item => item.AgentId).HasMaxLength(256);
            entity.Property(item => item.TraceId).HasMaxLength(256);
            entity.Property(item => item.Action).HasMaxLength(64);
            entity.Property(item => item.TargetCapability).HasMaxLength(512);
            entity.Property(item => item.RequestedBy).HasMaxLength(256);
            entity.Property(item => item.DecidedBy).HasMaxLength(256);
            entity.Property(item => item.MafRequestId).HasMaxLength(256);
            entity.Property(item => item.ToolCallId).HasMaxLength(256);
            entity.Property(item => item.ToolName).HasMaxLength(256);
            entity.Property(item => item.RedactedArgumentsJson).HasColumnType("jsonb");
            // MAF session state uses order-sensitive System.Text.Json metadata
            // properties (for example, "$type"). PostgreSQL jsonb normalizes
            // object property order and can therefore make a valid serialized
            // AgentSession impossible to deserialize after an approval pause.
            entity.Property(item => item.SessionStateJson).HasColumnType("text");
            entity.Property(item => item.RequesterContextJson).HasColumnType("jsonb");
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.Status, item.ExpiresAt });
            entity.HasIndex(item => new { item.TenantId, item.ConversationId });
            entity.HasOne<ConversationEntity>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
