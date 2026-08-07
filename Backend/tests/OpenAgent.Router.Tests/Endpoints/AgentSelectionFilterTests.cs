using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Security;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionFilterTests
{
    [Fact]
    public async Task InvokeAsync_NoExplicitAgent_UsesIntentAgentSelection()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        AgentSelectionFilter filter = CreateFilter(selector);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"find invoice\"}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        object? result = await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.NotNull(result);
        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("finance", feature.AgentId);
        Assert.True(feature.SelectedByIntentAgent);
        Assert.Equal(1, selector.CallCount);
        Assert.Equal(0, httpContext.Request.Body.Position);
        Assert.Equal("finance", httpContext.Response.Headers[AgentRoutingHeaders.SelectedAgentId]);
    }

    [Fact]
    public async Task InvokeAsync_ExplicitAgent_BypassesIntentAgentSelection()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        AgentSelectionFilter filter = CreateFilter(selector);
        DefaultHttpContext httpContext = CreateHttpContext(
            "{\"message\":\"hello\",\"context\":{\"agentId\":\"support\"}}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("support", feature.AgentId);
        Assert.False(feature.SelectedByIntentAgent);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_IntentAgentUnavailable_UsesConfiguredFallback()
    {
        var selector = new StubSelector(null);
        AgentSelectionFilter filter = CreateFilter(selector);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"hello\"}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("default", feature.AgentId);
        Assert.False(feature.SelectedByIntentAgent);
        Assert.Equal(1, selector.CallCount);
    }

    private static AgentSelectionFilter CreateFilter(StubSelector selector)
    {
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };
        return new AgentSelectionFilter(
            new StubRouteTable(),
            new AllowAllVisibilityService(),
            selector,
            user,
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                Enabled = true,
                AgentId = "intent-router",
                FallbackAgentId = "default",
                MinimumConfidence = 0.5,
                TimeoutMs = 5_000
            }),
            NullLogger<AgentSelectionFilter>.Instance);
    }

    private static DefaultHttpContext CreateHttpContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        return context;
    }

    private sealed class StubSelector(IntentAgentDecision? decision) : IIntentAgentSelector
    {
        public int CallCount { get; private set; }

        public Task<IntentAgentDecision?> SelectAsync(
            IntentAgentSelectionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(decision);
        }
    }

    private sealed class StubRouteTable : IRouteTable
    {
        public string? GetTargetEndpoint(string intent) => "http://engine";
    }

    private sealed class AllowAllVisibilityService : IAgentVisibilityService
    {
        public Task<bool> IsAgentVisibleToUserAsync(
            string agentId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<List<string>> GetPublishedAgentIdsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());

        public Task<string?> GetAgentConfigAsync(
            string agentId,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
