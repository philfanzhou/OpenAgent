using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using OpenAgent.Router.Security;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class AgentCatalogTests
{
    [Fact]
    public async Task ListAsync_IntentCandidates_MergesSourcesFiltersAclAndPrefersExternalConfig()
    {
        ExternalAgentRegistry external = CreateExternalRegistry(
            new ExternalAgentOptions
            {
                AgentId = "finance",
                Name = "Partner Finance",
                BaseUrl = "https://partner.example"
            },
            new ExternalAgentOptions
            {
                AgentId = "external-support",
                Name = "Partner Support",
                BaseUrl = "https://support.example"
            });
        var catalog = new AgentCatalog(
            new StubEngineClient(
            [
                new AgentSummary { AgentId = "finance", Name = "Engine Finance" },
                new AgentSummary { AgentId = "intent-router", Name = "Intent Router" },
                new AgentSummary { AgentId = "internal", Name = "Internal" }
            ]),
            external,
            new SelectiveVisibilityService("internal"),
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                AgentId = "intent-router",
                MaxCandidates = 100,
                TimeoutMs = 5_000,
                CatalogCacheSeconds = 15
            }),
            NullLogger<AgentCatalog>.Instance);

        IReadOnlyList<RoutableAgent> result = await catalog.ListAsync(
            CreateRequest(intentCandidatesOnly: true),
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Collection(
            result,
            agent => Assert.Equal("external-support", agent.Summary.AgentId),
            agent => Assert.Equal("finance", agent.Summary.AgentId));
        RoutableAgent finance = Assert.Single(result, agent => agent.Summary.AgentId == "finance");
        Assert.Equal("Partner Finance", finance.Summary.Name);
        Assert.Equal(AgentDestinationKind.External, finance.DestinationKind);
        Assert.Equal("https://partner.example", finance.TargetEndpoint);
    }

    [Fact]
    public async Task ListAsync_UserCatalog_IncludesIntentAgent()
    {
        var catalog = new AgentCatalog(
            new StubEngineClient(
            [
                new AgentSummary { AgentId = "intent-router", Name = "Intent Router" }
            ]),
            CreateExternalRegistry(),
            new SelectiveVisibilityService(),
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                AgentId = "intent-router",
                MaxCandidates = 1,
                TimeoutMs = 5_000,
                CatalogCacheSeconds = 15
            }),
            NullLogger<AgentCatalog>.Instance);

        IReadOnlyList<RoutableAgent> result = await catalog.ListAsync(
            CreateRequest(intentCandidatesOnly: false),
            CancellationToken.None);

        RoutableAgent agent = Assert.Single(result);
        Assert.Equal("intent-router", agent.Summary.AgentId);
    }

    private static AgentCatalogRequest CreateRequest(bool intentCandidatesOnly)
    {
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };
        return new AgentCatalogRequest(
            "http://engine",
            new DownstreamRequestIdentity(null, "tenant-1", null, "trace-1"),
            user,
            intentCandidatesOnly);
    }

    private static ExternalAgentRegistry CreateExternalRegistry(
        params ExternalAgentOptions[] agents) => new(
            Microsoft.Extensions.Options.Options.Create(new ExternalAgentRoutingOptions
            {
                Agents = agents.ToList()
            }));

    private sealed class StubEngineClient(IReadOnlyList<AgentSummary> agents) : IEngineAgentClient
    {
        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
            string engineEndpoint,
            DownstreamRequestIdentity identity,
            CancellationToken cancellationToken) => Task.FromResult(agents);

        public Task<string?> ChatAsync(
            string engineEndpoint,
            DownstreamRequestIdentity identity,
            string agentId,
            string message,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class SelectiveVisibilityService(params string[] denied) : IAgentVisibilityService
    {
        private readonly HashSet<string> _denied = new(denied, StringComparer.OrdinalIgnoreCase);

        public Task<bool> IsAgentVisibleToUserAsync(
            string agentId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(!_denied.Contains(agentId));

        public Task<List<string>> GetPublishedAgentIdsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());

        public Task<string?> GetAgentConfigAsync(
            string agentId,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
