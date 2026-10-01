using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Capabilities;
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
    [Theory]
    [InlineData("policy", 0)]
    [InlineData("policy", 11)]
    [InlineData("policy", "bad")]
    [InlineData("policy", null)]
    [InlineData("   ", 3)]
    public async Task Invoke_InvalidQueryOrLimit_RejectsBeforeSearch(string query, object? limit)
    {
        var service = new FakeRagService();
        var source = new RagCapabilitySource(service, NullLogger<RagCapabilitySource>.Instance);
        CapabilityDefinition capability = Assert.Single(await source.DiscoverAsync("agent",
            new AgentConfig { Rag = new RagConfig { Enabled = true } }, User(), default));
        ToolResult result = await capability.Invoke(new Dictionary<string, object?>
        {
            ["query"] = query, ["limit"] = limit
        }, default);

        Assert.True(result.IsError);
        Assert.Contains("invalid_arguments", result.Content, StringComparison.Ordinal);
        Assert.Null(service.LastQuery);
    }

    [Fact]
    public async Task Invoke_WithoutLimit_UsesDefaultAndPreservesUserContext()
    {
        var service = new FakeRagService();
        var user = User();
        var source = new RagCapabilitySource(service, NullLogger<RagCapabilitySource>.Instance);
        CapabilityDefinition capability = Assert.Single(await source.DiscoverAsync("agent",
            new AgentConfig { Rag = new RagConfig { Enabled = true } }, user, default));
        ToolResult result = await capability.Invoke(new Dictionary<string, object?> { ["query"] = "policy" }, default);

        Assert.Equal(3, service.LastLimit);
        Assert.Same(user, service.LastUser);
        Assert.False(result.IsError);
        Assert.Contains("No relevant information", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_RequestCancellation_Propagates()
    {
        var service = new FakeRagService();
        var source = new RagCapabilitySource(service, NullLogger<RagCapabilitySource>.Instance);
        CapabilityDefinition capability = Assert.Single(await source.DiscoverAsync("agent",
            new AgentConfig { Rag = new RagConfig { Enabled = true } }, User(), default));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capability.Invoke(
            new Dictionary<string, object?> { ["query"] = "policy" }, cancellation.Token));
    }

    [Fact]
    public async Task DiscoverAsync_EnabledRag_ExposesSearchCapability()
    {
        var service = new FakeRagService { Results = ["First", "Second"] };
        var source = new RagCapabilitySource(service, NullLogger<RagCapabilitySource>.Instance);

        IReadOnlyList<CapabilityDefinition> capabilities = await source.DiscoverAsync(
            "agent",
            new AgentConfig { Rag = new RagConfig { Enabled = true } },
            User(),
            default);
        CapabilityDefinition capability = Assert.Single(capabilities);
        ToolResult result = await capability.Invoke(
            new Dictionary<string, object?> { ["query"] = "policy", ["limit"] = 2 },
            default);

        Assert.Equal("search_knowledge_base", capability.Name);
        Assert.Equal("policy", service.LastQuery);
        Assert.Equal(2, service.LastLimit);
        Assert.Contains("1. First", result.Content);
        Assert.Contains("2. Second", result.Content);
    }

    [Fact]
    public async Task DiscoverAsync_DisabledRag_DoesNotExposeCapability()
    {
        var source = new RagCapabilitySource(new FakeRagService(), NullLogger<RagCapabilitySource>.Instance);

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
        public IAgentUserContext? LastUser { get; private set; }

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
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = query;
            LastLimit = limit;
            LastUser = userContext;
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
