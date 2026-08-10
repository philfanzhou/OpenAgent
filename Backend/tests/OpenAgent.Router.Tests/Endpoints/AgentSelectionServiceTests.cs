using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionServiceTests
{
    [Fact]
    public async Task SelectAsync_ExplicitAgent_BypassesIntentSelection()
    {
        var selector = new StubSelector("finance");
        AgentSelectionService service = CreateService(selector);

        string? result = await service.SelectAsync(
            CreateRequest(explicitAgentId: "support", conversationId: "conversation-1"),
            CancellationToken.None);

        Assert.Equal("support", result);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_ConversationId_ContinuesWithoutIntentSelection()
    {
        var selector = new StubSelector("finance");
        AgentSelectionService service = CreateService(selector);

        string? result = await service.SelectAsync(
            CreateRequest(conversationId: "conversation-1"),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_NoConversation_UsesIntentAgent()
    {
        var selector = new StubSelector("finance");
        AgentSelectionService service = CreateService(selector);

        string? result = await service.SelectAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal("finance", result);
        Assert.Equal(1, selector.CallCount);
        Assert.Equal("find invoice", selector.Request?.Query);
    }

    [Fact]
    public async Task SelectAsync_IntentAgentUnavailable_UsesFallback()
    {
        var selector = new StubSelector(null);
        AgentSelectionService service = CreateService(selector);

        string? result = await service.SelectAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal("default", result);
    }

    [Fact]
    public async Task SelectAsync_NoIntentOrFallback_ReturnsNoAgent()
    {
        AgentSelectionService service = CreateService(
            new StubSelector(null),
            fallbackAgentId: null);

        string? result = await service.SelectAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.Null(result);
    }

    private static AgentSelectionService CreateService(
        IIntentAgentSelector selector,
        string? fallbackAgentId = "default") =>
        new(
            selector,
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                Enabled = true,
                AgentId = "intent-router",
                FallbackAgentId = fallbackAgentId!,
                MinimumConfidence = 0.5,
                TimeoutMs = 5000
            }));

    private static AgentSelectionRequest CreateRequest(
        string? explicitAgentId = null,
        string? conversationId = null) =>
        new(
            "find invoice",
            "http://engine",
            conversationId,
            explicitAgentId,
            "Bearer token",
            "tenant-1",
            new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "tenant-1",
                IsAuthenticated = true
            });

    private sealed class StubSelector(string? result) : IIntentAgentSelector
    {
        public int CallCount { get; private set; }
        public AgentSelectionRequest? Request { get; private set; }

        public Task<string?> SelectAsync(
            AgentSelectionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(result);
        }
    }
}
