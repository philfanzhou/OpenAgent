using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Security;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionServiceTests
{
    [Fact]
    public async Task SelectAsync_ExplicitAgent_BypassesIntentSelection()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        AgentSelectionService service = CreateService(selector);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(explicitAgentId: "support", conversationId: "conversation-1"),
            CancellationToken.None);

        Assert.True(result.IsSelected);
        Assert.Equal("support", result.AgentId);
        Assert.False(result.SelectedByIntentAgent);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_ConversationId_ContinuesWithoutLookupOrIntentSelection()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        AgentSelectionService service = CreateService(selector);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(conversationId: "conversation-1"),
            CancellationToken.None);

        Assert.Equal(AgentSelectionStatus.ContinueConversation, result.Status);
        Assert.True(result.CanForward);
        Assert.Null(result.AgentId);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_NoConversation_UsesIntentAgent()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.92, null));
        AgentSelectionService service = CreateService(selector);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.True(result.IsSelected);
        Assert.Equal("finance", result.AgentId);
        Assert.True(result.SelectedByIntentAgent);
        Assert.Equal(0.92, result.Confidence);
        Assert.Equal(1, selector.CallCount);
        Assert.Equal("find invoice", selector.Request?.Query);
    }

    [Fact]
    public async Task SelectAsync_IntentAgentUnavailable_UsesFallback()
    {
        var selector = new StubSelector(null);
        AgentSelectionService service = CreateService(selector);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.True(result.IsSelected);
        Assert.Equal("default", result.AgentId);
        Assert.False(result.SelectedByIntentAgent);
        Assert.Equal(1, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_SelectedAgentNotVisible_ReturnsForbidden()
    {
        AgentSelectionService service = CreateService(new StubSelector(null), visible: false);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(explicitAgentId: "private"),
            CancellationToken.None);

        Assert.Equal(AgentSelectionStatus.Forbidden, result.Status);
        Assert.False(result.IsSelected);
    }

    [Fact]
    public async Task SelectAsync_NoIntentOrFallback_ReturnsNoAgentAvailable()
    {
        AgentSelectionService service = CreateService(
            new StubSelector(null),
            fallbackAgentId: null);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal(AgentSelectionStatus.NoAgentAvailable, result.Status);
    }

    private static AgentSelectionService CreateService(
        IIntentAgentSelector selector,
        bool visible = true,
        string? fallbackAgentId = "default") =>
        new(
            new StubVisibilityService(visible),
            selector,
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                Enabled = true,
                AgentId = "intent-router",
                FallbackAgentId = fallbackAgentId!,
                MinimumConfidence = 0.5,
                TimeoutMs = 5000
            }),
            NullLogger<AgentSelectionService>.Instance);

    private static AgentSelectionRequest CreateRequest(
        string? explicitAgentId = null,
        string? conversationId = null) =>
        new(
            "find invoice",
            "http://engine",
            conversationId,
            explicitAgentId,
            new EngineRequestIdentity("Bearer token", "tenant-1", "engine"),
            new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "tenant-1",
                IsAuthenticated = true
            },
            "trace-1");

    private sealed class StubSelector(IntentAgentDecision? decision) : IIntentAgentSelector
    {
        public int CallCount { get; private set; }
        public IntentAgentSelectionRequest? Request { get; private set; }

        public Task<IntentAgentDecision?> SelectAsync(
            IntentAgentSelectionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(decision);
        }
    }

    private sealed class StubVisibilityService(bool visible) : IAgentVisibilityService
    {
        public Task<bool> IsAgentVisibleToUserAsync(
            string agentId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(visible);

        public Task<List<string>> GetPublishedAgentIdsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());

        public Task<string?> GetAgentConfigAsync(
            string agentId,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
