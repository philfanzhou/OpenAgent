using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
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
    public async Task ResolveAsync_AgentTokenDefaultExceedsModelCapability_ThrowsConfigurationError()
    {
        AgentConfig config = new()
        {
            Llm = new LlmConfig
            {
                Provider = "profile-1",
                ContextWindowTokens = 129_000
            }
        };
        AgentRuntimeResolver resolver = CreateResolver(config, new LlmProviderProfile
        {
            Id = "profile-1",
            TenantId = "tenant-1",
            Endpoint = "https://llm.example.test",
            ApiKey = "test-key",
            ContextWindowTokens = 128_000,
            MaxOutputTokens = 16_000
        });

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            resolver.ResolveAsync("agent-1", User(), CancellationToken.None));

        Assert.Equal(AgentErrorCode.ConfigurationError, exception.ErrorCode);
        Assert.Contains("exceeds model capability", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_ProviderDoesNotSupportAgentOutputDefault_ThrowsConfigurationError()
    {
        AgentConfig config = new()
        {
            Llm = new LlmConfig
            {
                Provider = "profile-1",
                MaxOutputTokens = 4_000
            }
        };
        AgentRuntimeResolver resolver = CreateResolver(config, new LlmProviderProfile
        {
            Id = "profile-1",
            TenantId = "tenant-1",
            Endpoint = "https://llm.example.test",
            ApiKey = "test-key",
            ContextWindowTokens = 128_000,
            MaxOutputTokens = 16_000,
            SupportsMaxOutputTokens = false
        });

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            resolver.ResolveAsync("agent-1", User(), CancellationToken.None));

        Assert.Equal(AgentErrorCode.ConfigurationError, exception.ErrorCode);
        Assert.Contains("does not support", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private static AgentRuntimeResolver CreateResolver(
        AgentConfig config,
        LlmProviderProfile profile)
    {
        LlmRegistry models = new();
        models.Register(profile);
        return new AgentRuntimeResolver(
            new StaticConfigProvider(config),
            new AgentAuthorizationGate(
                new AllowAllAgentAuthorizationService(),
                models));
    }

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
