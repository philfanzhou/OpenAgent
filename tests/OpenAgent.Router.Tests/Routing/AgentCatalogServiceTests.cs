using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Router;
using OpenAgent.Router.Models;
using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class AgentCatalogServiceTests
{
    [Fact]
    public async Task GetAuthorizedAsync_SkipsUnavailableProviderAndKeepsHealthyAgents()
    {
        var healthy = new StubProvider(
            "healthy",
            new AgentProviderCatalog([new AgentSummary { AgentId = "general" }]));
        var unavailable = new StubProvider(
            "unavailable",
            new AgentProviderCatalog([], false));
        var service = new AgentCatalogService(
            new StubProviderRegistry([healthy, unavailable]),
            []);

        IReadOnlyList<AgentCatalogEntry> entries = await service.GetAuthorizedAsync(
            RequestContext(),
            CancellationToken.None);

        AgentCatalogEntry entry = Assert.Single(entries);
        Assert.Equal("general", entry.Agent.AgentId);
        Assert.Equal("healthy", entry.ProviderId);
    }

    [Fact]
    public async Task GetAuthorizedAsync_WhenAllProvidersUnavailable_ReturnsUnavailable()
    {
        var service = new AgentCatalogService(
            new StubProviderRegistry(
            [
                new StubProvider("first", new AgentProviderCatalog([], false)),
                new StubProvider("second", new AgentProviderCatalog([], false))
            ]),
            []);

        AgentRoutingException exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.GetAuthorizedAsync(RequestContext(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
        Assert.Equal(RouterErrorCodes.AgentProviderUnavailable, exception.Code);
    }

    private static AgentProviderRequestContext RequestContext() => new(
        new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        });

    private sealed class StubProvider(
        string id,
        AgentProviderCatalog catalog) : IAgentProvider
    {
        public string Id => id;

        public Task<AgentProviderCatalog> GetAgentsAsync(
            AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken) => Task.FromResult(catalog);

        public Task<AgentProviderConversationStatus> ResolveConversationAsync(
            AgentProviderRequestContext requestContext,
            string conversationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AgentProviderConversationStatus.NotFound);

        public Task<IntentRecognitionResult?> RecognizeIntentAsync(
            AgentProviderRequestContext requestContext,
            string intentAgentId,
            IReadOnlyList<AgentSummary> agents,
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
        IReadOnlyList<IAgentProvider> providers) : IAgentProviderRegistry
    {
        public IReadOnlyList<IAgentProvider> Providers => providers;

        public IAgentProvider DefaultProvider => providers[0];

        public bool TryGet(string providerId, out IAgentProvider? provider)
        {
            provider = providers.FirstOrDefault(item => string.Equals(
                item.Id,
                providerId,
                StringComparison.OrdinalIgnoreCase));
            return provider != null;
        }
    }
}
