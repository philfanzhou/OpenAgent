using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionFilterTests
{
    [Fact]
    public async Task InvokeAsync_ValidRequest_MapsSelectionAndPreservesBody()
    {
        var selectionService = new StubSelectionService("finance");
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"find invoice\"}");
        httpContext.Request.Headers.Authorization = "Basic credential";
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        object? result = await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.NotNull(result);
        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("http://engine", feature.TargetEndpoint);
        Assert.Equal(0, httpContext.Request.Body.Position);
        Assert.Equal("finance", httpContext.Request.Headers["X-Agent-Id"]);
        Assert.NotNull(selectionService.Request);
        Assert.Equal("find invoice", selectionService.Request.Query);
        Assert.Equal("http://engine", selectionService.Request.TargetEndpoint);
        Assert.Equal("Basic credential", selectionService.Request.Authorization);
    }

    [Fact]
    public async Task InvokeAsync_ExplicitAgent_MapsAgentIdToSelectionRequest()
    {
        var selectionService = new StubSelectionService("support");
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext httpContext = CreateHttpContext(
            "{\"message\":\"hello\",\"context\":{\"agentId\":\"support\"}}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.NotNull(selectionService.Request);
        Assert.Equal("support", selectionService.Request.ExplicitAgentId);
        Assert.Equal("support", httpContext.Request.Headers["X-Agent-Id"]);
    }

    [Fact]
    public async Task InvokeAsync_ConversationHeader_PreservesAffinityWithoutMarkingFollowUp()
    {
        var selectionService = new StubSelectionService("finance");
        var routeTable = new StubRouteTable();
        AgentSelectionFilter filter = CreateFilter(selectionService, routeTable);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"follow up\"}");
        httpContext.Request.Headers["X-Conversation-Id"] = "conversation-from-header";
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal("conversation-from-header", routeTable.ConversationId);
        Assert.Null(selectionService.Request?.ConversationId);
    }

    [Fact]
    public async Task InvokeAsync_ConversationContinuation_DoesNotWriteAgentHeader()
    {
        var selectionService = new StubSelectionService(null);
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext httpContext = CreateHttpContext(
            "{\"message\":\"follow up\",\"context\":{\"conversationId\":\"conversation-1\"}}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal("conversation-1", selectionService.Request?.ConversationId);
        Assert.False(httpContext.Request.Headers.ContainsKey("X-Agent-Id"));
        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("conversation-1", feature.ConversationId);
    }

    [Fact]
    public async Task InvokeAsync_NoAgentSelected_ReturnsServiceUnavailable()
    {
        var selectionService = new StubSelectionService(null);
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"hello\"}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        object? result = await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        IStatusCodeHttpResult statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_JsonWithInvalidShape_ReturnsBadRequest()
    {
        var selectionService = new StubSelectionService("finance");
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext httpContext = CreateHttpContext("[]");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        object? result = await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        IStatusCodeHttpResult statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, statusResult.StatusCode);
        Assert.Null(selectionService.Request);
        Assert.Equal(0, httpContext.Request.Body.Position);
    }

    private static AgentSelectionFilter CreateFilter(
        IAgentSelectionService selectionService,
        StubRouteTable? routeTable = null)
    {
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };
        return new AgentSelectionFilter(
            routeTable ?? new StubRouteTable(),
            selectionService,
            user);
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

    private sealed class StubSelectionService(string? result) : IAgentSelectionService
    {
        public AgentSelectionRequest? Request { get; private set; }

        public Task<string?> SelectAsync(
            AgentSelectionRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }

    private sealed class StubRouteTable : IRouteTable
    {
        public string? ConversationId { get; private set; }

        public string? GetTargetEndpoint(string intent) => "http://engine";

        public string? GetTargetEndpoint(
            string intent,
            string? tenantId,
            string? conversationId)
        {
            ConversationId = conversationId;
            return "http://engine";
        }
    }
}
