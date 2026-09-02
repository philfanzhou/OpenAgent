using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Core.Capabilities.UserProfile;
using OpenAgent.Core.Exten;
using OpenAgent.Core.Conversation.Store;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class CapabilityServiceRegistrationTests
{
    [Fact]
    public async Task AddAgentCore_ResolvesConsolidatedCapabilitySources()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentConfigProvider, StaticConfigProvider>();
        services.AddSingleton<ILlmConfigProvider, StaticLlmConfigProvider>();
        services.AddSingleton<ICurrentUserContext, TestUserContext>();
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddSingleton<IFileAssetRepository, EmptyFileAssetRepository>();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddSingleton(configuration);
        services.AddAgentCore(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IEnumerable<ICapabilitySource> sources = scope.ServiceProvider
            .GetRequiredService<IEnumerable<ICapabilitySource>>();

        Assert.Contains(sources, source => source is RagCapabilitySource);
        Assert.Contains(sources, source => source is UserProfileCapabilitySource);
        Assert.DoesNotContain(sources, source => source.GetType().Name.Contains("HttpSkill", StringComparison.Ordinal));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AgentSkillsProviderFactory>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<McpToolFactory>());
    }

    private sealed class StaticConfigProvider : IAgentConfigProvider
    {
        public Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentConfig());

        public Task<AgentConfig?> GetConfigAsync(
            string agentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentConfig?>(new AgentConfig());

        public Task<AgentConfig?> GetConfigAsync(
            string agentId,
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentConfig?>(new AgentConfig { TenantId = tenantId });

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>([]);

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>([]);
    }

    private sealed class StaticLlmConfigProvider : ILlmConfigProvider
    {
        public Task<LlmProviderProfile?> GetAsync(
            string tenantId,
            string profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmProviderProfile?>(null);

        public Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmProviderProfile>>([]);
    }

    private sealed class EmptyFileAssetRepository : IFileAssetRepository
    {
        public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) =>
            Task.FromResult<FileAsset?>(null);
        public Task<IReadOnlyList<FileAsset>> ListReferencedAsync(
            string conversationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FileAsset>>([]);
        public Task EnsureConversationReferencesAsync(
            string conversationId,
            IReadOnlyList<string> fileIds,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> IsReferencedAsync(
            string conversationId,
            string fileId,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class TestUserContext : ICurrentUserContext
    {
        public string UserId => "test";
        public string? TenantId => "test-tenant";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }
}
