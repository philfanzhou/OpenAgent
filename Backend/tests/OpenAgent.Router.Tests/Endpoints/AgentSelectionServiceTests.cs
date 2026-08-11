using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionServiceTests
{
    [Fact]
    public async Task SelectAsync_ExplicitAgent_ResolvesAuthorizedProvider()
    {
        var engine = new StubProvider("self-engine", [Agent("general")]);
        var partner = new StubProvider("partner", [Agent("finance")]);
        AgentSelectionService service = CreateService([engine, partner]);

        AgentSelection? selection = await service.SelectAsync(
            "hello",
            null,
            "finance",
            CancellationToken.None);

        Assert.Equal("finance", selection?.AgentId);
        Assert.Equal("partner", selection?.ProviderId);
    }

    [Fact]
    public async Task SelectAsync_ExplicitUnauthorizedAgent_ReturnsNotFound()
    {
        var engine = new StubProvider("self-engine", [Agent("general"), Agent("finance")]);
        AgentSelectionService service = CreateService(
            [engine],
            accessControls: [new AllowOnlyAccessControl("general")]);

        AgentRoutingException exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync(
                "hello",
                null,
                "finance",
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal(RouterErrorCodes.AgentNotFound, exception.Code);
    }

    [Fact]
    public async Task SelectAsync_ExistingConversationOnPartner_BackfillsConfirmedAffinity()
    {
        var engine = new StubProvider("self-engine", [Agent("general")]);
        var partner = new StubProvider("partner", [Agent("finance")]);
        partner.Conversations["conversation-1"] = AgentProviderConversationStatus.Found;
        var store = new StubConversationProviderStore();
        var selector = new StubSelector("general");
        AgentSelectionService service = CreateService([engine, partner], selector, store);

        AgentSelection? selection = await service.SelectAsync(
            "follow up",
            "conversation-1",
            null,
            CancellationToken.None);

        Assert.Null(selection?.AgentId);
        Assert.Equal("partner", selection?.ProviderId);
        ConversationProviderAffinity? affinity = await store.GetAsync(
            "tenant-1",
            "conversation-1");
        Assert.NotNull(affinity);
        Assert.Equal(ConversationAffinityState.Confirmed, affinity.State);
        Assert.Equal(0, selector.CallCount);
        Assert.Equal("tenant-1", partner.LastTenantId);
    }

    [Fact]
    public async Task SelectAsync_NewConversation_UsesSelectedProviderAndBindsPendingAffinity()
    {
        var engine = new StubProvider("self-engine", [Agent("general")]);
        var partner = new StubProvider("partner", [Agent("finance")]);
        var store = new StubConversationProviderStore();
        AgentSelectionService service = CreateService(
            [engine, partner],
            new StubSelector("finance"),
            store);

        AgentSelection? selection = await service.SelectAsync(
            "find invoice",
            "conversation-new",
            null,
            CancellationToken.None);

        Assert.Equal("finance", selection?.AgentId);
        Assert.Equal("partner", selection?.ProviderId);
        ConversationProviderAffinity? affinity = await store.GetAsync(
            "tenant-1",
            "conversation-new");
        Assert.NotNull(affinity);
        Assert.Equal(ConversationAffinityState.Pending, affinity.State);
    }

    [Fact]
    public async Task SelectAsync_ConfirmedConversationMigrated_UpdatesProviderAffinity()
    {
        var engine = new StubProvider("self-engine", [Agent("general")]);
        var partner = new StubProvider("partner", [Agent("finance")]);
        partner.Conversations["conversation-1"] = AgentProviderConversationStatus.Found;
        var store = new StubConversationProviderStore();
        await store.SetAsync(
            "tenant-1",
            "conversation-1",
            new ConversationProviderAffinity("self-engine", ConversationAffinityState.Confirmed));
        AgentSelectionService service = CreateService([engine, partner], store: store);

        AgentSelection? selection = await service.SelectAsync(
            "follow up",
            "conversation-1",
            null,
            CancellationToken.None);

        Assert.Equal("partner", selection?.ProviderId);
        Assert.Equal(
            "partner",
            (await store.GetAsync("tenant-1", "conversation-1"))?.ProviderId);
    }

    [Fact]
    public async Task SelectAsync_ConversationAndExplicitAgentOnDifferentProviders_ReturnsConflict()
    {
        var engine = new StubProvider("self-engine", [Agent("general")]);
        var partner = new StubProvider("partner", [Agent("finance")]);
        partner.Conversations["conversation-1"] = AgentProviderConversationStatus.Found;
        AgentSelectionService service = CreateService([engine, partner]);

        AgentRoutingException exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync(
                "follow up",
                "conversation-1",
                "general",
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(RouterErrorCodes.ConversationProviderMismatch, exception.Code);
    }

    [Fact]
    public async Task SelectAsync_ConflictingAgentIds_ReturnsConflict()
    {
        var engine = new StubProvider("self-engine", [Agent("support")]);
        var partner = new StubProvider("partner", [Agent("support")]);
        AgentSelectionService service = CreateService([engine, partner]);

        AgentRoutingException exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync(
                "hello",
                null,
                "support",
                CancellationToken.None));

        Assert.Equal(RouterErrorCodes.AgentIdConflict, exception.Code);
    }

    [Fact]
    public async Task SelectAsync_ConflictingConversationOwners_ReturnsConflict()
    {
        var engine = new StubProvider("self-engine", [Agent("general")]);
        var partner = new StubProvider("partner", [Agent("finance")]);
        engine.Conversations["conversation-1"] = AgentProviderConversationStatus.Found;
        partner.Conversations["conversation-1"] = AgentProviderConversationStatus.Found;
        AgentSelectionService service = CreateService([engine, partner]);

        AgentRoutingException exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync(
                "follow up",
                "conversation-1",
                null,
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(RouterErrorCodes.ConversationOwnerConflict, exception.Code);
    }

    [Fact]
    public async Task SelectAsync_KnownProviderUnavailable_DoesNotChangeAffinity()
    {
        var engine = new StubProvider("self-engine", [Agent("general")])
        {
            DefaultConversationStatus = AgentProviderConversationStatus.Unavailable
        };
        var partner = new StubProvider("partner", [Agent("finance")]);
        var store = new StubConversationProviderStore();
        await store.SetAsync(
            "tenant-1",
            "conversation-1",
            new ConversationProviderAffinity("self-engine", ConversationAffinityState.Confirmed));
        AgentSelectionService service = CreateService([engine, partner], store: store);

        AgentRoutingException exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync(
                "follow up",
                "conversation-1",
                null,
                CancellationToken.None));

        Assert.Equal(RouterErrorCodes.AgentProviderUnavailable, exception.Code);
        Assert.Equal(
            "self-engine",
            (await store.GetAsync("tenant-1", "conversation-1"))?.ProviderId);
    }

    [Fact]
    public async Task SelectAsync_IntentHasNoDecision_UsesFallbackAgentProvider()
    {
        var engine = new StubProvider("self-engine", []);
        var partner = new StubProvider("partner", [Agent("general")]);
        AgentSelectionService service = CreateService(
            [engine, partner],
            new StubSelector(null));

        AgentSelection? selection = await service.SelectAsync(
            "hello",
            null,
            null,
            CancellationToken.None);

        Assert.Equal("general", selection?.AgentId);
        Assert.Equal("partner", selection?.ProviderId);
    }

    [Fact]
    public async Task SelectAsync_UnboundConversationWithUnavailableProvider_DoesNotFallback()
    {
        var engine = new StubProvider("self-engine", [Agent("general")]);
        var partner = new StubProvider("partner", [Agent("finance")])
        {
            DefaultConversationStatus = AgentProviderConversationStatus.Unavailable
        };
        AgentSelectionService service = CreateService([engine, partner]);

        AgentRoutingException exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync(
                "follow up",
                "conversation-1",
                null,
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
        Assert.Equal(RouterErrorCodes.ConversationOwnerUnresolved, exception.Code);
    }

    private static AgentSelectionService CreateService(
        IReadOnlyList<IAgentProvider> providers,
        StubSelector? selector = null,
        StubConversationProviderStore? store = null,
        IReadOnlyList<IAgentAccessControl>? accessControls = null)
    {
        var registry = new StubProviderRegistry(providers[0], providers);
        var affinityStore = store ?? new StubConversationProviderStore();
        return new AgentSelectionService(
            new AgentCatalogService(registry, accessControls ?? []),
            new ConversationProviderResolver(registry, affinityStore),
            selector ?? new StubSelector("general"),
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
                FallbackAgentId = "general",
                MinimumConfidence = 0.5,
                TimeoutMs = 5000
            }));
    }

    private static AgentSummary Agent(string agentId) => new() { AgentId = agentId };

    private sealed class StubProvider(
        string id,
        IReadOnlyList<AgentSummary> agents) : IAgentProvider
    {
        public string Id => id;

        public Dictionary<string, AgentProviderConversationStatus> Conversations { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public AgentProviderConversationStatus DefaultConversationStatus { get; init; } =
            AgentProviderConversationStatus.NotFound;

        public string? LastTenantId { get; private set; }

        public Task<AgentProviderCatalog> GetAgentsAsync(
            AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentProviderCatalog(agents));

        public Task<AgentProviderConversationStatus> ResolveConversationAsync(
            AgentProviderRequestContext requestContext,
            string conversationId,
            CancellationToken cancellationToken)
        {
            LastTenantId = requestContext.UserContext.TenantId;
            AgentProviderConversationStatus status = Conversations.TryGetValue(
                conversationId,
                out AgentProviderConversationStatus configured)
                ? configured
                : DefaultConversationStatus;
            return Task.FromResult(status);
        }

        public Task<IntentRecognitionResult?> RecognizeIntentAsync(
            string intentAgentId,
            IReadOnlyList<AgentSummary> candidates,
            string message,
            IAgentUserContext userContext,
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
            provider = providers.FirstOrDefault(item => string.Equals(
                item.Id,
                providerId,
                StringComparison.OrdinalIgnoreCase));
            return provider != null;
        }
    }

    private sealed class StubSelector(string? result) : IIntentAgentSelector
    {
        public int CallCount { get; private set; }

        public Task<string?> SelectAsync(
            string message,
            IReadOnlyList<AgentSummary> candidates,
            IAgentUserContext userContext,
            CancellationToken cancellationToken)
        {
            CallCount++;
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

    private sealed class StubConversationProviderStore : IConversationProviderStore
    {
        private readonly Dictionary<string, ConversationProviderAffinity> _affinities =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<ConversationProviderAffinity?> GetAsync(
            string tenantId,
            string conversationId,
            CancellationToken cancellationToken = default)
        {
            _affinities.TryGetValue($"{tenantId}|{conversationId}", out ConversationProviderAffinity? affinity);
            return Task.FromResult(affinity);
        }

        public Task SetAsync(
            string tenantId,
            string conversationId,
            ConversationProviderAffinity affinity,
            CancellationToken cancellationToken = default)
        {
            _affinities[$"{tenantId}|{conversationId}"] = affinity;
            return Task.CompletedTask;
        }

        public Task<ConversationProviderAffinity> BindAsync(
            string tenantId,
            string conversationId,
            ConversationProviderAffinity affinity,
            CancellationToken cancellationToken = default)
        {
            string key = $"{tenantId}|{conversationId}";
            if (!_affinities.TryGetValue(key, out ConversationProviderAffinity? bound))
            {
                _affinities[key] = affinity;
                bound = affinity;
            }

            return Task.FromResult(bound);
        }
    }
}
