using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class IntentAgentSelectorTests
{
    private static readonly IReadOnlyList<AgentSummary> Candidates =
    [
        new AgentSummary { AgentId = "finance", Name = "Finance" },
        new AgentSummary { AgentId = "support", Name = "Support" }
    ];

    [Fact]
    public void ParseDecision_ValidFencedJson_NormalizesAgentId()
    {
        const string content = """
            ```json
            {"agentId":"FINANCE","confidence":0.91,"reason":"invoice request"}
            ```
            """;

        IntentAgentDecision? decision = IntentAgentSelector.ParseDecision(content, Candidates, 0.5);

        Assert.NotNull(decision);
        Assert.Equal("finance", decision.AgentId);
        Assert.Equal(0.91, decision.Confidence);
    }

    [Theory]
    [InlineData("{\"agentId\":\"unknown\",\"confidence\":0.9}")]
    [InlineData("{\"agentId\":\"finance\",\"confidence\":0.2}")]
    [InlineData("{\"agentId\":\"finance\",\"confidence\":1.1}")]
    [InlineData("")]
    public void ParseDecision_InvalidSelection_ReturnsNull(string content)
    {
        IntentAgentDecision? decision = IntentAgentSelector.ParseDecision(content, Candidates, 0.5);

        Assert.Null(decision);
    }

    [Fact]
    public async Task SelectAsync_SendsCandidatesToConfiguredIntentAgent()
    {
        var client = new RecordingEngineAgentClient();
        var selector = new IntentAgentSelector(
            client,
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                AgentId = "intent-router",
                MinimumConfidence = 0.5,
                TimeoutMs = 5_000,
                MaxCandidates = 100,
                MaxMessageCharacters = 16_000
            }),
            NullLogger<IntentAgentSelector>.Instance);
        IntentAgentDecision? decision = await selector.SelectAsync(
            new IntentAgentSelectionRequest(
                "find my invoice",
                "http://engine",
                new DownstreamRequestIdentity(
                    "Basic token",
                    "tenant-1",
                    "router",
                    "trace-1"),
                [
                    new AgentSummary
                    {
                        AgentId = "finance",
                        Name = "Finance",
                        Description = "Handles invoices"
                    }
                ]),
            CancellationToken.None);

        Assert.NotNull(decision);
        Assert.Equal("finance", decision.AgentId);
        Assert.Equal("http://engine", client.EngineEndpoint);
        Assert.Equal("intent-router", client.AgentId);
        Assert.Contains("Handles invoices", client.Message, StringComparison.Ordinal);
        Assert.Contains("find my invoice", client.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingEngineAgentClient : IEngineAgentClient
    {
        public string? EngineEndpoint { get; private set; }
        public string? AgentId { get; private set; }
        public string Message { get; private set; } = string.Empty;

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
            string engineEndpoint,
            DownstreamRequestIdentity identity,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentSummary>>([]);

        public Task<string?> ChatAsync(
            string engineEndpoint,
            DownstreamRequestIdentity identity,
            string agentId,
            string message,
            CancellationToken cancellationToken)
        {
            EngineEndpoint = engineEndpoint;
            AgentId = agentId;
            Message = message;
            return Task.FromResult<string?>("{\"agentId\":\"finance\",\"confidence\":0.95}");
        }
    }
}
