using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionServiceTests
{
    [Fact]
    public async Task SelectAsync_ExplicitAgent_BypassesCatalogAndIntentSelection()
    {
        var engine = new StubProvider("self-engine", []);
        var selector = new StubSelector("finance");
        AgentSelectionService service = CreateService(engine, selector);

        AgentSelection? selection = await service.SelectAsync(
            "hello",
            null,
            "support",
            CancellationToken.None);

        Assert.Equal("support", selection?.AgentId);
        Assert.Equal("self-engine", selection?.ProviderId);
        Assert.Equal(0, engine.GetAgentsCallCount);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_ConversationId_BypassesCatalogAndIntentSelection()
    {
        var engine = new StubProvider("self-engine", []);
        var selector = new StubSelector("finance");
        AgentSelectionService service = CreateService(engine, selector);

        AgentSelection? selection = await service.SelectAsync(
            "follow up",
            "conversation-1",
            null,
            CancellationToken.None);

        Assert.Null(selection?.AgentId);
        Assert.Equal("self-engine", selection?.ProviderId);
        Assert.Equal(0, engine.GetAgentsCallCount);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_IntentSelection_UsesCandidateProvider()
    {
        var engine = new StubProvider("self-engine",
        [
            new AgentSummary { AgentId = "general", Name = "General" }
        ]);
        var partner = new StubProvider("partner",
        [
            new AgentSummary { AgentId = "finance", Name = "Finance" }
        ]);
        var selector = new StubSelector("finance");
        AgentSelectionService service = CreateService(engine, selector, partner);

        AgentSelection? selection = await service.SelectAsync(
            "find invoice",
            null,
            null,
            CancellationToken.None);

        Assert.Equal("finance", selection?.AgentId);
        Assert.Equal("partner", selection?.ProviderId);
        Assert.Equal(["finance", "general"], selector.Candidates.Select(x => x.AgentId));
    }

    [Fact]
    public async Task SelectAsync_AccessControl_FiltersBeforeIntentSelection()
    {
        var engine = new StubProvider("self-engine",
        [
            new AgentSummary { AgentId = "general" },
            new AgentSummary { AgentId = "finance" }
        ]);
        var selector = new StubSelector("general");
        AgentSelectionService service = CreateService(
            engine,
            selector,
            accessControls: [new AllowOnlyAccessControl("general")]);

        await service.SelectAsync(
            "hello",
            null,
            null,
            CancellationToken.None);

        Assert.Equal("general", Assert.Single(selector.Candidates).AgentId);
    }

    [Fact]
    public async Task SelectAsync_IntentUnavailable_UsesFallbackCandidateProvider()
    {
        var engine = new StubProvider("self-engine", []);
        var partner = new StubProvider("partner",
        [
            new AgentSummary { AgentId = "default" }
        ]);
        AgentSelectionService service = CreateService(
            engine,
            new StubSelector(null),
            partner);

        AgentSelection? selection = await service.SelectAsync(
            "hello",
            null,
            null,
            CancellationToken.None);

        Assert.Equal("default", selection?.AgentId);
        Assert.Equal("partner", selection?.ProviderId);
    }

    [Fact]
    public async Task SelectAsync_NoFallback_ReturnsNull()
    {
        var engine = new StubProvider("self-engine", []);
        AgentSelectionService service = CreateService(
            engine,
            new StubSelector(null),
            fallbackAgentId: null);

        AgentSelection? selection = await service.SelectAsync(
            "hello",
            null,
            null,
            CancellationToken.None);

        Assert.Null(selection);
    }

    private static AgentSelectionService CreateService(
        StubProvider defaultProvider,
        IIntentAgentSelector selector,
        StubProvider? additionalProvider = null,
        IReadOnlyList<IAgentAccessControl>? accessControls = null,
        string? fallbackAgentId = "default")
    {
        IAgentProvider[] providers = additionalProvider == null
            ? [defaultProvider]
            : [defaultProvider, additionalProvider];
        return new AgentSelectionService(
            new StubProviderRegistry(defaultProvider, providers),
            accessControls ?? [],
            selector,
            new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "tenant-1",
                IsAuthenticated = true
            },
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                Enabled = true,
                ProviderId = "self-engine",
                AgentId = "intent-router",
                FallbackAgentId = fallbackAgentId!,
                MinimumConfidence = 0.5,
                TimeoutMs = 5000
            }));
    }

    private sealed class StubProvider(
        string id,
        IReadOnlyList<AgentSummary> agents) : IAgentProvider
    {
        public string Id => id;
        public int GetAgentsCallCount { get; private set; }

        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(
            CancellationToken cancellationToken)
        {
            GetAgentsCallCount++;
            return Task.FromResult(agents);
        }

        public Task<IntentRecognitionResult?> RecognizeIntentAsync(
            string intentAgentId,
            IReadOnlyList<AgentSummary> candidates,
            string message,
            CancellationToken cancellationToken) =>
            Task.FromResult<IntentRecognitionResult?>(null);

        public Task<AgentForwardingTarget?> ResolveForwardingAsync(
            string? action,
            string? tenantId,
            string? conversationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AgentForwardingTarget?>(null);

        public ValueTask ConfigureRequestAsync(
            HttpRequestMessage request,
            AgentForwardingTarget target,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class StubProviderRegistry(
        IAgentProvider defaultProvider,
        IReadOnlyList<IAgentProvider> providers) : IAgentProviderRegistry
    {
        public IReadOnlyList<IAgentProvider> Providers => providers;
        public IAgentProvider DefaultProvider => defaultProvider;

        public bool TryGet(string providerId, out IAgentProvider? provider)
        {
            provider = providers.FirstOrDefault(item => item.Id == providerId);
            return provider != null;
        }
    }

    private sealed class StubSelector(string? result) : IIntentAgentSelector
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<AgentSummary> Candidates { get; private set; } = [];

        public Task<string?> SelectAsync(
            string message,
            IReadOnlyList<AgentSummary> candidates,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Candidates = candidates;
            return Task.FromResult(result);
        }
    }

    private sealed class AllowOnlyAccessControl(string agentId) : IAgentAccessControl
    {
        public Task<IReadOnlyList<AgentSummary>> GetAuthorizedAgentsAsync(
            IAgentUserContext userContext,
            IReadOnlyList<AgentSummary> agents,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>(
                agents.Where(agent => agent.AgentId == agentId).ToArray());
    }
}
