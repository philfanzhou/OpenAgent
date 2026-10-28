using System.Text;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionFilterTests
{
    [Fact]
    public async Task InvokeAsync_ValidRequest_WritesAgentHeaderAndProviderFeature()
    {
        var selectionService = new StubSelectionService(
            new AgentSelection("finance", "partner"));
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext context = CreateContext("{\"message\":\"find invoice\"}");

        await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal("finance", context.Request.Headers["X-Agent-Id"]);
        Assert.Equal("finance", context.Response.Headers["X-OpenAgent-Selected-Agent-Id"]);
        Assert.Equal(0, context.Request.Body.Position);
        AgentRoutingFeature feature = Assert.IsType<AgentRoutingFeature>(
            context.Features.Get<AgentRoutingFeature>());
        Assert.Equal("partner", feature.ProviderId);
        Assert.Equal("find invoice", selectionService.Message);
    }

    [Fact]
    public async Task InvokeAsync_BodyAgentId_OverwritesExistingHeader()
    {
        var selectionService = new StubSelectionService(
            new AgentSelection("support", "self-engine"));
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext context = CreateContext(
            "{\"message\":\"hello\",\"context\":{\"agentId\":\"support\"}}");
        context.Request.Headers["X-Agent-Id"] = "old-agent";

        await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal("support", selectionService.ExplicitAgentId);
        Assert.Equal("support", context.Request.Headers["X-Agent-Id"]);
    }

    [Fact]
    public async Task InvokeAsync_GinaAgentHeader_IsUsedForExplicitSelection()
    {
        var selectionService = new StubSelectionService(
            new AgentSelection("general", "gina"));
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext context = CreateContext("{\"message\":\"hello\"}");
        context.Request.Headers["X-Gina-Agent-Id"] = "general";

        await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal("general", selectionService.ExplicitAgentId);
        Assert.Equal("general", context.Request.Headers["X-Agent-Id"]);
    }

    [Fact]
    public async Task InvokeAsync_ConversationHeader_IsPassedToSelection()
    {
        var selectionService = new StubSelectionService(
            new AgentSelection(null, "self-engine"));
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext context = CreateContext("{\"message\":\"follow up\"}");
        context.Request.Headers["X-Conversation-Id"] = "conversation-1";

        await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal("conversation-1", selectionService.ConversationId);
        Assert.False(context.Request.Headers.ContainsKey("X-Agent-Id"));
        Assert.Equal(
            "conversation-1",
            context.Features.Get<AgentRoutingFeature>()?.ConversationId);
    }

    [Fact]
    public async Task InvokeAsync_NoSelection_ReturnsServiceUnavailable()
    {
        AgentSelectionFilter filter = CreateFilter(new StubSelectionService(null));
        DefaultHttpContext context = CreateContext("{\"message\":\"hello\"}");

        object? result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        IStatusCodeHttpResult status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_InvalidJson_ReturnsBadRequestAndRewindsBody()
    {
        var selectionService = new StubSelectionService(
            new AgentSelection("finance", "self-engine"));
        AgentSelectionFilter filter = CreateFilter(selectionService);
        DefaultHttpContext context = CreateContext("[]");

        object? result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        IStatusCodeHttpResult status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
        Assert.Null(selectionService.Message);
        Assert.Equal(0, context.Request.Body.Position);
    }

    private static AgentSelectionFilter CreateFilter(IAgentSelectionService selectionService) =>
        new(
            selectionService,
            new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "tenant-1",
                IsAuthenticated = true
            });

    private static DefaultHttpContext CreateContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        return context;
    }

    private sealed class StubSelectionService(AgentSelection? result) : IAgentSelectionService
    {
        public string? Message { get; private set; }
        public string? ConversationId { get; private set; }
        public string? ExplicitAgentId { get; private set; }

        public Task<AgentSelection?> SelectAsync(
            string message,
            string? conversationId,
            string? explicitAgentId,
            CancellationToken cancellationToken,
            string? authenticationToken = null,
            string? llmProfileId = null)
        {
            Message = message;
            ConversationId = conversationId;
            ExplicitAgentId = explicitAgentId;
            return Task.FromResult(result);
        }
    }
}
