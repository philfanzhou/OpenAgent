using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Models;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class AgentRuntimeResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsAuthorizedProfileAndConversationPolicy()
    {
        AgentConfig config = new()
        {
            Llm = new LlmConfig
            {
                Provider = "profile-1",
                ModelId = "model-1",
                Temperature = 0.2
            },
            ContextPolicy = new ContextPolicy
            {
                Strategy = "sliding_window",
                MaxTokens = 2_000
            },
            MaxTurns = 8
        };
        StaticConfigProvider configs = new(config);
        LlmRegistry models = new();
        models.Register(new LlmProviderProfile
        {
            TenantId = "tenant-1",
            Id = "profile-1",
            Endpoint = "https://llm.example.test",
            ApiKey = "test-key"
        });
        AgentRuntimeResolver resolver = new(
            configs,
            new AgentAuthorizationGate(
                new AllowAllAgentAuthorizationService(),
                models));

        AgentRuntimeProfile result = await resolver.ResolveAsync(
            "agent-1",
            User(),
            CancellationToken.None);

        Assert.Equal("agent-1", result.AgentId);
        Assert.Same(config, result.Config);
        Assert.Same(config.ContextPolicy, result.Config.ContextPolicy);
        Assert.Equal("https://llm.example.test", result.Model.Endpoint);
        Assert.Equal("test-key", result.Model.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_InvalidMaxTurns_ThrowsBeforeAgentCreation()
    {
        AgentConfig config = new()
        {
            MaxTurns = -1,
            Llm = new LlmConfig { Provider = "profile-1" }
        };
        LlmRegistry models = new();
        models.Register(new LlmProviderProfile
        {
            TenantId = "tenant-1",
            Id = "profile-1",
            Endpoint = "https://llm.example.test",
            ApiKey = "test-key"
        });
        AgentRuntimeResolver resolver = new(
            new StaticConfigProvider(config),
            new AgentAuthorizationGate(
                new AllowAllAgentAuthorizationService(),
                models));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("agent-1", User(), CancellationToken.None));

        Assert.Contains("MaxTurns", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_MissingConfig_Throws()
    {
        AgentRuntimeResolver resolver = new(
            new StaticConfigProvider(null),
            new AgentAuthorizationGate(
                new AllowAllAgentAuthorizationService(),
                new LlmRegistry()));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("missing", User(), CancellationToken.None));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedContextPolicy_Throws()
    {
        AgentConfig config = new()
        {
            ContextPolicy = new ContextPolicy { Strategy = "unknown" },
            Llm = new LlmConfig { Provider = "profile-1" }
        };
        LlmRegistry models = new();
        models.Register(new LlmProviderProfile
        {
            TenantId = "tenant-1",
            Id = "profile-1",
            Endpoint = "https://llm.example.test",
            ApiKey = "test-key"
        });
        AgentRuntimeResolver resolver = new(
            new StaticConfigProvider(config),
            new AgentAuthorizationGate(
                new AllowAllAgentAuthorizationService(),
                models));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("agent-1", User(), CancellationToken.None));

        Assert.Contains("Unsupported ContextPolicy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_SkillBindingOwnedByAnotherTenant_HidesAgentConfiguration()
    {
        var config = new AgentConfig
        {
            TenantId = "tenant-a",
            Skills = new SkillsConfig { EnabledSkills = ["lookup"] }
        };
        AgentRuntimeResolver resolver = new(
            new StaticConfigProvider(config),
            new AgentAuthorizationGate(
                new AllowAllAgentAuthorizationService(),
                new LlmRegistry()));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(
                "agent-a",
                new AgentUserContext { UserId = "user-b", TenantId = "tenant-b" },
                CancellationToken.None));
        Assert.Contains("agent-a", exception.Message, StringComparison.Ordinal);
    }

    private static AgentUserContext User() => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1"
    };

    private sealed class StaticConfigProvider : IAgentConfigProvider
    {
        private readonly AgentConfig? _config;

        public StaticConfigProvider(AgentConfig? config)
        {
            _config = config;
            if (_config != null)
            {
                _config.TenantId = "tenant-1";
            }
        }

        public Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_config ?? new AgentConfig());

        public Task<AgentConfig?> GetConfigAsync(
            string agentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_config);

        public Task<AgentConfig?> GetConfigAsync(
            string agentId,
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _config != null
                && string.Equals(_config.TenantId, tenantId, StringComparison.Ordinal)
                    ? _config
                    : null);

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>([]);

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>([]);
    }
}
