using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Core.Capabilities.UserProfile;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Exten;
using OpenAgent.Core.Tests.TestDoubles;
using OpenAgent.Core.Tooling.Abstractions;
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
        services.AddSingleton<ILlmInteractionStore, EmptyInteractionStore>();
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

    [Fact]
    public void AddAgentCore_RepeatedRegistration_DoesNotAppendOptionsOrFeatureFactories()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["FileAssets:AllowedExtensions:0"] = ".txt"
            }).Build();
        services.AddAgentCore(configuration);
        int registrations = services.Count;
        using ServiceProvider first = services.BuildServiceProvider();
        string[] extensions = first.GetRequiredService<IOptions<FileAssetOptions>>().Value.AllowedExtensions.ToArray();

        services.AddAgentCore(configuration);
        using ServiceProvider repeated = services.BuildServiceProvider();

        Assert.Equal(registrations, services.Count);
        Assert.Equal(extensions, repeated.GetRequiredService<IOptions<FileAssetOptions>>().Value.AllowedExtensions);
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(IAgentFeatureFactory)));
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

    private sealed class EmptyInteractionStore : ILlmInteractionStore
    {
        public Task RecordAsync(LlmInteractionRecord record, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LlmInteractionRecord>> ListAsync(
            string tenantId,
            string conversationId,
            int skip,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmInteractionRecord>>([]);
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
