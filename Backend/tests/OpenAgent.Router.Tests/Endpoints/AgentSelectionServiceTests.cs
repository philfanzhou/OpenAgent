using System.Net;
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
    public async Task SelectAsync_ExplicitAgent_BypassesConversationAndIntentSelection()
    {
        var resolver = new StubConversationAgentResolver(ConversationAgentResolution.NotFound);
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        AgentSelectionService service = CreateService(resolver, selector);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(explicitAgentId: "support", conversationId: "conversation-1"),
            CancellationToken.None);

        Assert.True(result.IsSelected);
        Assert.Equal("support", result.AgentId);
        Assert.False(result.SelectedByIntentAgent);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_ExistingConversation_ReusesBoundAgent()
    {
        var resolver = new StubConversationAgentResolver(
            new ConversationAgentResolution(true, "finance"));
        var selector = new StubSelector(new IntentAgentDecision("support", 0.9, null));
        AgentSelectionService service = CreateService(resolver, selector);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(conversationId: "conversation-1"),
            CancellationToken.None);

        Assert.True(result.IsSelected);
        Assert.Equal("finance", result.AgentId);
        Assert.False(result.SelectedByIntentAgent);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, selector.CallCount);
        Assert.Equal("Bearer token", resolver.Identity?.Authorization);
    }

    [Fact]
    public async Task SelectAsync_NewConversation_UsesIntentAgent()
    {
        var resolver = new StubConversationAgentResolver(ConversationAgentResolution.NotFound);
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.92, null));
        AgentSelectionService service = CreateService(resolver, selector);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(conversationId: "new-conversation"),
            CancellationToken.None);

        Assert.True(result.IsSelected);
        Assert.Equal("finance", result.AgentId);
        Assert.True(result.SelectedByIntentAgent);
        Assert.Equal(0.92, result.Confidence);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, selector.CallCount);
        Assert.Equal("find invoice", selector.Request?.Query);
    }

    [Fact]
    public async Task SelectAsync_IntentAgentUnavailable_UsesFallback()
    {
        var selector = new StubSelector(null);
        AgentSelectionService service = CreateService(
            new StubConversationAgentResolver(ConversationAgentResolution.NotFound),
            selector);

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
        AgentSelectionService service = CreateService(
            new StubConversationAgentResolver(ConversationAgentResolution.NotFound),
            new StubSelector(null),
            visible: false);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(explicitAgentId: "private"),
            CancellationToken.None);

        Assert.Equal(AgentSelectionFailure.Forbidden, result.Failure);
        Assert.False(result.IsSelected);
    }

    [Fact]
    public async Task SelectAsync_ConversationAccessDenied_ReturnsForbidden()
    {
        var resolver = new StubConversationAgentResolver(
            new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden));
        AgentSelectionService service = CreateService(resolver, new StubSelector(null));

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(conversationId: "conversation-1"),
            CancellationToken.None);

        Assert.Equal(AgentSelectionFailure.Forbidden, result.Failure);
    }

    [Fact]
    public async Task SelectAsync_ConversationDependencyFailure_DoesNotRerouteConversation()
    {
        var resolver = new StubConversationAgentResolver(new HttpRequestException("unavailable"));
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        AgentSelectionService service = CreateService(resolver, selector);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(conversationId: "conversation-1"),
            CancellationToken.None);

        Assert.Equal(AgentSelectionFailure.DependencyUnavailable, result.Failure);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task SelectAsync_NoIntentOrFallback_ReturnsNoAgentAvailable()
    {
        AgentSelectionService service = CreateService(
            new StubConversationAgentResolver(ConversationAgentResolution.NotFound),
            new StubSelector(null),
            fallbackAgentId: null);

        AgentSelectionResult result = await service.SelectAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal(AgentSelectionFailure.NoAgentAvailable, result.Failure);
    }

    private static AgentSelectionService CreateService(
        IConversationAgentResolver resolver,
        IIntentAgentSelector selector,
        bool visible = true,
        string? fallbackAgentId = "default") =>
        new(
            new StubVisibilityService(visible),
            resolver,
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

    private sealed class StubConversationAgentResolver : IConversationAgentResolver
    {
        private readonly ConversationAgentResolution _resolution;
        private readonly Exception? _exception;

        internal StubConversationAgentResolver(ConversationAgentResolution resolution)
        {
            _resolution = resolution;
        }

        internal StubConversationAgentResolver(Exception exception)
        {
            _exception = exception;
        }

        public int CallCount { get; private set; }
        public EngineRequestIdentity? Identity { get; private set; }

        public Task<ConversationAgentResolution> ResolveAsync(
            string targetEndpoint,
            string conversationId,
            EngineRequestIdentity identity,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Identity = identity;
            return _exception == null
                ? Task.FromResult(_resolution)
                : Task.FromException<ConversationAgentResolution>(_exception);
        }
    }

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
