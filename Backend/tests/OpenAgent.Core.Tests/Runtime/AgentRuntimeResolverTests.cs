using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class AgentRuntimeResolverTests
{
    [Fact]
    public async Task ResolveAsync_CombinesAgentWithSelectedTenantModel()
    {
        AgentConfig config = new()
        {
            ContextPolicy = new ContextPolicy { PreserveRecentTurns = 4 },
            MaxTurns = 8
        };
        AgentRuntimeResolver resolver = CreateResolver(config, Profile());

        AgentRuntimeProfile result = await resolver.ResolveAsync(
            "agent-1", "profile-1", User(), CancellationToken.None);

        Assert.Same(config, result.Config);
        Assert.Equal("model-1", result.Model.ModelId);
        Assert.Equal("test-key", result.Model.ApiKey);
        Assert.Equal(ModelModality.Multimodal, result.Model.Modality);
        Assert.Equal(4, result.Config.ContextPolicy?.PreserveRecentTurns);
    }

    [Fact]
    public async Task ResolveAsync_MissingSelectedModel_Throws()
    {
        AgentRuntimeResolver resolver = CreateResolver(new AgentConfig(), null);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("agent-1", "missing", User(), CancellationToken.None));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_InvalidMaxTurns_Throws()
    {
        AgentRuntimeResolver resolver = CreateResolver(new AgentConfig { MaxTurns = -1 }, Profile());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("agent-1", "profile-1", User(), CancellationToken.None));

        Assert.Contains("MaxTurns", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_DifferentTenant_DoesNotExposeAgent()
    {
        AgentRuntimeResolver resolver = CreateResolver(new AgentConfig(), Profile());
        AgentUserContext foreignUser = new() { UserId = "user-2", TenantId = "tenant-2" };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("agent-1", "profile-1", foreignUser, CancellationToken.None));

        Assert.Contains("agent-1", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, 128000, 16000)]
    [InlineData(64000, 4000, 64000, 4000)]
    [InlineData(null, 4000, 128000, 4000)]
    public async Task ResolveAsync_AppliesAgentDefaultsWithoutChangingSelectedProfile(
        int? context, int? output, int expectedContext, int expectedOutput)
    {
        LlmProviderProfile model = Profile();
        model.MaxOutputTokens = 16000;
        AgentRuntimeResolver resolver = CreateResolver(new AgentConfig
        {
            ContextWindowTokens = context,
            MaxOutputTokens = output
        }, model);

        AgentRuntimeProfile result = await resolver.ResolveAsync("agent-1", "profile-1", User());

        Assert.Equal(expectedContext, result.Model.ContextTokens);
        Assert.Equal(expectedOutput, result.Model.MaxOutputTokens);
        Assert.Equal(128000, result.Model.TokenCapabilities.ContextWindowTokens);
        Assert.Equal(16000, result.Model.TokenCapabilities.MaxOutputTokens);
        Assert.Equal(128000, model.ContextTokens);
        Assert.Equal(16000, model.MaxOutputTokens);
        Assert.Equal(ModelModality.Multimodal, result.Model.Modality);
    }

    [Theory]
    [InlineData(0, null, true)]
    [InlineData(null, -1, true)]
    [InlineData(128001, null, true)]
    [InlineData(null, 16001, true)]
    [InlineData(16000, null, true)]
    [InlineData(null, 4000, false)]
    public async Task ResolveAsync_InvalidAgentDefaults_ThrowsConfigurationError(
        int? context, int? output, bool supported)
    {
        LlmProviderProfile model = Profile();
        model.MaxOutputTokens = 16000;
        model.SupportsMaxOutputTokens = supported;
        AgentRuntimeResolver resolver = CreateResolver(new AgentConfig
        {
            ContextWindowTokens = context,
            MaxOutputTokens = output
        }, model);

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            resolver.ResolveAsync("agent-1", "profile-1", User()));

        Assert.Equal(OpenAgent.Contracts.Requests.AgentErrorCode.ConfigurationError, exception.ErrorCode);
    }

    private static AgentRuntimeResolver CreateResolver(
        AgentConfig? config,
        LlmProviderProfile? profile) => new(
        new StaticConfigProvider(config),
        new StaticLlmConfigProvider(profile),
        new AgentAuthorizationGate(new AllowAllAgentAuthorizationService()));

    private static LlmProviderProfile Profile() => new()
    {
        TenantId = "tenant-1",
        Id = "profile-1",
        ModelId = "model-1",
        ContextTokens = 128_000,
        Endpoint = "https://llm.example.test",
        ApiKey = "test-key",
        Modality = ModelModality.Multimodal
    };

    private static AgentUserContext User() => new() { UserId = "user-1", TenantId = "tenant-1" };

    private sealed class StaticConfigProvider : IAgentConfigProvider
    {
        private readonly AgentConfig? _config;

        public StaticConfigProvider(AgentConfig? config)
        {
            _config = config;
            if (_config != null) _config.TenantId = "tenant-1";
        }

        public Task<AgentConfig?> GetConfigAsync(
            string agentId,
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(tenantId, _config?.TenantId, StringComparison.Ordinal)
                ? _config
                : null);

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>([]);
    }

    private sealed class StaticLlmConfigProvider(LlmProviderProfile? profile) : ILlmConfigProvider
    {
        public Task<LlmProviderProfile?> GetAsync(
            string tenantId,
            string profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                string.Equals(tenantId, profile?.TenantId, StringComparison.Ordinal)
                && string.Equals(profileId, profile!.Id, StringComparison.Ordinal)
                    ? profile
                    : null);

        public Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmProviderProfile>>([]);
    }
}
