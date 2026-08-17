using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentCatalogEndpointHandlerTests
{
    [Fact]
    public async Task HandleAsync_AuthorizedCatalog_ReturnsAgentsWithoutProviderIds()
    {
        var catalog = new StubCatalogService(new AgentCatalogSnapshot(
        [
            new AgentCatalogEntry(new AgentSummary { AgentId = "finance" }, "partner"),
            new AgentCatalogEntry(new AgentSummary { AgentId = "general" }, "self-engine")
        ]));

        IResult result = await AgentCatalogEndpointHandler.HandleAsync(
            catalog,
            AuthenticatedUser,
            CancellationToken.None);

        IValueHttpResult valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        IEnumerable<AgentSummary> agents = Assert.IsAssignableFrom<IEnumerable<AgentSummary>>(
            valueResult.Value);
        Assert.Equal(["finance", "general"], agents.Select(agent => agent.AgentId));
        Assert.DoesNotContain(
            agents.SelectMany(agent => agent.GetType().GetProperties()),
            property => property.Name.Equals("ProviderId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_AnonymousUser_ReturnsUnauthorized()
    {
        IResult result = await AgentCatalogEndpointHandler.HandleAsync(
            new StubCatalogService(new AgentCatalogSnapshot([])),
            new AgentUserContext { UserId = "anonymous", IsAuthenticated = false },
            CancellationToken.None);

        IStatusCodeHttpResult status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status.StatusCode);
    }

    private static AgentUserContext AuthenticatedUser => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };

    private sealed class StubCatalogService(AgentCatalogSnapshot snapshot) : IAgentCatalogService
    {
        public Task<AgentCatalogSnapshot> GetAuthorizedAsync(
            AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);

        public Task<AgentCatalogEntry> ResolveAsync(
            AgentProviderRequestContext requestContext,
            string agentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot.Entries.Single(entry => entry.Agent.AgentId == agentId));
    }
}
