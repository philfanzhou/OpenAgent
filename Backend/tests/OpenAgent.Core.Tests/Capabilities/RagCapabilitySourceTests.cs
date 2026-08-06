using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Rag;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class RagCapabilitySourceTests
{
    [Fact]
    public async Task DiscoverAsync_EnabledRag_ExposesSearchCapability()
    {
        var service = new FakeRagService { Results = ["First", "Second"] };
        var source = new RagCapabilitySource(service);

        IReadOnlyList<CapabilityDefinition> capabilities = await source.DiscoverAsync(
            "agent",
            new AgentConfig { Rag = new RagConfig { Enabled = true } },
            User(),
            default);
        CapabilityDefinition capability = Assert.Single(capabilities);
        string result = await capability.Invoke(
            new Dictionary<string, object?> { ["query"] = "policy", ["limit"] = 2 },
            default);

        Assert.Equal("search_knowledge_base", capability.Name);
        Assert.Equal("policy", service.LastQuery);
        Assert.Equal(2, service.LastLimit);
        Assert.Contains("1. First", result);
        Assert.Contains("2. Second", result);
    }

    [Fact]
    public async Task DiscoverAsync_DisabledRag_DoesNotExposeCapability()
    {
        var source = new RagCapabilitySource(new FakeRagService());

        IReadOnlyList<CapabilityDefinition> capabilities = await source.DiscoverAsync(
            "agent",
            new AgentConfig { Rag = new RagConfig { Enabled = false } },
            User(),
            default);

        Assert.Empty(capabilities);
    }

    private static AgentUserContext User() => new() { UserId = "user" };

    private sealed class FakeRagService : IRagService
    {
        public List<string> Results { get; init; } = [];
        public string? LastQuery { get; private set; }
        public int LastLimit { get; private set; }

        public Task IndexDocumentAsync(
            string content,
            Dictionary<string, object>? metadata,
            string? ragInstanceId,
            RagConfig config,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<List<string>> SearchAsync(
            string query,
            int limit,
            RagConfig config,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            LastLimit = limit;
            return Task.FromResult(Results);
        }

        public Task<List<SearchResult>> SearchDetailedAsync(
            string query,
            int limit,
            RagConfig config,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<SearchResult>());
    }
}
