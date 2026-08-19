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
        Assert.Contains(sources, source => source is HttpSkillCapabilitySource);
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

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>([]);
    }

    private sealed class EmptyFileAssetRepository : IFileAssetRepository
    {
        public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) => Task.FromResult<FileAsset?>(null);
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
