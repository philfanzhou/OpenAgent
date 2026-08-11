using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class IntentAgentSelectorTests
{
    private static readonly IReadOnlyList<AgentSummary> Candidates =
    [
        new AgentSummary
        {
            AgentId = "finance",
            Name = "Finance",
            Description = "Handles invoices"
        }
    ];

    [Theory]
    [InlineData("finance", 0.9, "finance")]
    [InlineData("FINANCE", 0.9, "finance")]
    [InlineData("unknown", 0.9, null)]
    [InlineData("finance", 0.1, null)]
    public void ValidateResult_ProviderResult_ValidatesCandidateAndConfidence(
        string agentId,
        double confidence,
        string? expected)
    {
        string? decision = IntentAgentSelector.ValidateResult(
            new IntentRecognitionResult(agentId, confidence),
            Candidates,
            0.5);

        Assert.Equal(expected, decision);
    }

    [Fact]
    public async Task SelectAsync_InvokesConfiguredProviderAndAgent()
    {
        var provider = new RecordingProvider();
        var selector = new IntentAgentSelector(
            new StubProviderRegistry(provider),
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                ProviderId = "intent-provider",
                AgentId = "intent-router",
                MinimumConfidence = 0.5,
                TimeoutMs = 5000
            }));

        string? decision = await selector.SelectAsync(
            "find my invoice",
            Candidates,
            new AgentUserContext { UserId = "user-1", TenantId = "tenant-1", IsAuthenticated = true },
            CancellationToken.None);

        Assert.Equal("finance", decision);
        Assert.Equal("intent-router", provider.AgentId);
        AgentSummary agent = Assert.Single(provider.Agents);
        Assert.Equal("finance", agent.AgentId);
        Assert.Equal("Handles invoices", agent.Description);
        Assert.Equal("find my invoice", provider.Message);
    }

    private sealed class RecordingProvider : IAgentProvider
    {
        public string Id => "intent-provider";
        public string? AgentId { get; private set; }
        public IReadOnlyList<AgentSummary> Agents { get; private set; } = [];
        public string Message { get; private set; } = string.Empty;

        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(
            IAgentUserContext userContext,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>([]);

        public Task<IntentRecognitionResult?> RecognizeIntentAsync(
            string intentAgentId,
            IReadOnlyList<AgentSummary> agents,
            string message,
            IAgentUserContext userContext,
            CancellationToken cancellationToken)
        {
            AgentId = intentAgentId;
            Agents = agents;
            Message = message;
            return Task.FromResult<IntentRecognitionResult?>(
                new IntentRecognitionResult("finance", 0.95));
        }

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

    private sealed class StubProviderRegistry(IAgentProvider provider) : IAgentProviderRegistry
    {
        public IReadOnlyList<IAgentProvider> Providers => [provider];
        public IAgentProvider DefaultProvider => provider;

        public bool TryGet(string providerId, out IAgentProvider? result)
        {
            result = providerId == provider.Id ? provider : null;
            return result != null;
        }
    }
}
