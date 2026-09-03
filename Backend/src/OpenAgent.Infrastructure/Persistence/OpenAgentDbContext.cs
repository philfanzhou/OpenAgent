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
    internal DbSet<ThirdPartyApiKeyEntity> ThirdPartyApiKeys => Set<ThirdPartyApiKeyEntity>();
    internal DbSet<AgentConfigurationEntity> AgentConfigurations => Set<AgentConfigurationEntity>();
    internal DbSet<LlmConfigurationEntity> LlmConfigurations => Set<LlmConfigurationEntity>();

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

        modelBuilder.Entity<ThirdPartyApiKeyEntity>(entity =>
        {
            entity.ToTable("third_party_api_keys");
            entity.HasKey(item => item.ApiKeyId);
            entity.Property(item => item.ApiKeyId).HasMaxLength(64);
            entity.Property(item => item.Name).HasMaxLength(256);
            entity.Property(item => item.KeyHash).HasMaxLength(64);
            entity.Property(item => item.UserId).HasMaxLength(256);
            entity.Property(item => item.TenantId).HasMaxLength(256);
            entity.Property(item => item.Username).HasMaxLength(256);
            entity.Property(item => item.Email).HasMaxLength(512);
            entity.Property(item => item.Scopes).HasMaxLength(2048);
            entity.Property(item => item.Roles).HasMaxLength(2048);
            entity.Property(item => item.Groups).HasMaxLength(2048);
            entity.HasIndex(item => item.KeyHash).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.IsEnabled });
            entity.HasData(
                new ThirdPartyApiKeyEntity
                {
                    ApiKeyId = "demo-partner-a",
                    Name = "Demo Partner A",
                    KeyHash = "1F0EDBABFE0BDAF41574D36AA8530D39233A8C832A5AF7B975E7784D6939C5A7",
                    UserId = "integration:partner-a",
                    TenantId = "tenant-a",
                    Username = "partner-a",
                    Scopes = "agent.execute model.invoke",
                    IsEnabled = true,
                    CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                },
                new ThirdPartyApiKeyEntity
                {
                    ApiKeyId = "demo-partner-b",
                    Name = "Demo Partner B",
                    KeyHash = "46504FDCB1197B4268C79C2594C72B5FC02A0D03F7795F6B96B2B56386C0426F",
                    UserId = "integration:partner-b",
                    TenantId = "tenant-b",
                    Username = "partner-b",
                    Scopes = "agent.execute model.invoke",
                    IsEnabled = true,
                    CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                });
        });

        modelBuilder.Entity<AgentConfigurationEntity>(entity =>
        {
            entity.ToTable("agent_configurations");
            entity.HasKey(item => new { item.TenantId, item.AgentId });
            entity.Property(item => item.AgentId).HasMaxLength(256);
            entity.Property(item => item.TenantId).HasMaxLength(256);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ContextPolicyJson).HasColumnType("jsonb");
            entity.Property(item => item.McpJson).HasColumnType("jsonb");
            entity.Property(item => item.RagJson).HasColumnType("jsonb");
            entity.Property(item => item.SkillsJson).HasColumnType("jsonb");
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.HasIndex(item => new { item.TenantId, item.UpdatedAt });
        });

        modelBuilder.Entity<LlmConfigurationEntity>(entity =>
        {
            entity.ToTable("llm_configurations");
            entity.HasKey(item => new { item.TenantId, item.ProfileId });
            entity.Property(item => item.TenantId).HasMaxLength(256);
            entity.Property(item => item.ProfileId).HasMaxLength(256);
            entity.Property(item => item.Format).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Modality).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(item => new { item.TenantId, item.UpdatedAt });
        });
    }
}
