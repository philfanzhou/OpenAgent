using System.Net;
using System.Net.Http.Json;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ConversationAgentResolverTests
{
    [Fact]
    public async Task ResolveAsync_ExistingConversation_ReturnsBoundAgent()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                conversationId = "conversation-1",
                agentId = "finance"
            })
        });
        var resolver = new ConversationAgentResolver(new HttpClient(handler));
        ConversationAgentResolution resolution = await resolver.ResolveAsync(
            "http://engine",
            "conversation-1",
            new EngineRequestIdentity("Basic credential", "tenant-1", "engine"),
            CancellationToken.None);

        Assert.True(resolution.Exists);
        Assert.Equal("finance", resolution.AgentId);
        Assert.Equal(
            "http://engine/api/v1/agent/conversations/conversation-1/route",
            handler.RequestUri);
        Assert.Equal("Basic credential", handler.Authorization);
    }

    [Fact]
    public async Task ResolveAsync_MissingConversation_ReturnsNotFound()
    {
        var resolver = new ConversationAgentResolver(new HttpClient(
            new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NotFound))));

        ConversationAgentResolution resolution = await resolver.ResolveAsync(
            "http://engine",
            "new-conversation",
            new EngineRequestIdentity(null, null, null),
            CancellationToken.None);

        Assert.False(resolution.Exists);
        Assert.Null(resolution.AgentId);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(response);
        }
    }
}
