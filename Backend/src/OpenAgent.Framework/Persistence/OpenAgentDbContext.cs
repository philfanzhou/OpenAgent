using Microsoft.EntityFrameworkCore;
using OpenAgent.Framework.Entities;
using OpenAgent.Framework.Persistence.Mapping;

namespace OpenAgent.Framework;

public sealed class OpenAgentDbContext(DbContextOptions<OpenAgentDbContext> options) : DbContext(options)
{
    internal DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    internal DbSet<ConversationMessageEntity> ConversationMessages => Set<ConversationMessageEntity>();
    internal DbSet<FileAssetEntity> FileAssets => Set<FileAssetEntity>();
    internal DbSet<FileShareLinkEntity> FileShareLinks => Set<FileShareLinkEntity>();
    internal DbSet<ConversationFileReferenceEntity> ConversationFileReferences => Set<ConversationFileReferenceEntity>();
    internal DbSet<MessageFileReferenceEntity> MessageFileReferences => Set<MessageFileReferenceEntity>();
    internal DbSet<SkillDefinitionEntity> SkillDefinitions => Set<SkillDefinitionEntity>();
    internal DbSet<ThirdPartyApiKeyEntity> ThirdPartyApiKeys => Set<ThirdPartyApiKeyEntity>();
    internal DbSet<AgentConfigurationEntity> AgentConfigurations => Set<AgentConfigurationEntity>();
    internal DbSet<LlmConfigurationEntity> LlmConfigurations => Set<LlmConfigurationEntity>();
    internal DbSet<LlmInteractionEntity> LlmInteractions => Set<LlmInteractionEntity>();

    internal DbSet<RuntimeConfigurationEntity> RuntimeConfigurations => Set<RuntimeConfigurationEntity>();
    internal DbSet<McpConfigurationEntity> McpConfigurations => Set<McpConfigurationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("openagent");
        modelBuilder.ApplyConfiguration(new RuntimeConfigurationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new McpConfigurationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationMessageEntityConfiguration());
        modelBuilder.ApplyConfiguration(new LlmInteractionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new FileAssetEntityConfiguration());
        modelBuilder.ApplyConfiguration(new FileShareLinkEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationFileReferenceEntityConfiguration());
        modelBuilder.ApplyConfiguration(new MessageFileReferenceEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SkillDefinitionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ThirdPartyApiKeyEntityConfiguration());
        modelBuilder.ApplyConfiguration(new AgentConfigurationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new LlmConfigurationEntityConfiguration());
    }
}
