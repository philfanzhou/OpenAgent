using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class EndpointExtensionsTests
{
    private static DefaultHttpContext CreateContext(
        string? traceId = null,
        IAgentUserContext? user = null)
    {
        var context = new DefaultHttpContext();
        var feature = new AgentRequestFeature(
            traceId ?? "trace-1",
            user ?? new AgentUserContext { UserId = "anonymous" });
        context.Features.Set(feature);
        return context;
    }

    [Fact]
    public void CreateAgentRequest_ContextPopulated_ReadsIdsFromHttpContext()
    {
        var context = CreateContext();
        context.Request.Headers["X-Agent-Id"] = "agent-from-header";
        context.Request.Headers["X-Conversation-Id"] = "conv-from-header";

        var request = new ChatRequest
        {
            Message = "hello",
            ContextWindowTokens = 64_000,
            MaxOutputTokens = 4_000,
            Context = new Dictionary<string, object>
            {
                ["agentId"] = "body-agent",
                ["conversationId"] = "body-conv",
                ["conversationType"] = "Channel",
                ["clientType"] = "Teams",
                ["customKey"] = "custom-value"
            }
        };

        var agentRequest = AgentEndpointRequestMapper.CreateAgentRequest(request, context);

        Assert.Equal("hello", agentRequest.Query);
        Assert.Equal("body-agent", agentRequest.AgentId);
        Assert.Equal("body-conv", agentRequest.ConversationId);
        Assert.Equal(ConversationType.Channel, agentRequest.ConversationType);
        Assert.Equal(ClientType.Teams, agentRequest.ClientType);
        Assert.Equal(64_000, agentRequest.ContextWindowTokens);
        Assert.Equal(4_000, agentRequest.MaxOutputTokens);
        Assert.Equal("trace-1", agentRequest.TraceId);
        Assert.NotNull(agentRequest.ExternalContext);
        Assert.Equal("custom-value", agentRequest.ExternalContext!["customKey"]);
        Assert.False(agentRequest.ExternalContext.ContainsKey("agentId"));
        Assert.False(agentRequest.ExternalContext.ContainsKey("conversationId"));
        Assert.False(agentRequest.ExternalContext.ContainsKey("conversationType"));
        Assert.False(agentRequest.ExternalContext.ContainsKey("clientType"));
    }

    [Fact]
    public void CreateAgentRequest_NoChatContext_ExternalContextNull()
    {
        var context = CreateContext(traceId: "trace-2");
        var request = new ChatRequest { Message = "hi" };

        var agentRequest = AgentEndpointRequestMapper.CreateAgentRequest(request, context);

        Assert.Null(agentRequest.AgentId);
        Assert.False(string.IsNullOrWhiteSpace(agentRequest.ConversationId));
        Assert.Equal("trace-2", agentRequest.TraceId);
        Assert.Null(agentRequest.ExternalContext);
    }

    [Fact]
    public void CreateAgentRequest_FallsBackToHeaders_WhenNotInBody()
    {
        var context = CreateContext();
        context.Request.Headers["X-Agent-Id"] = "header-agent";

        var request = new ChatRequest { Message = "ping" };

        var agentRequest = AgentEndpointRequestMapper.CreateAgentRequest(request, context);

        Assert.Equal("header-agent", agentRequest.AgentId);
    }

    [Fact]
    public void CreateAgentRequest_WithoutConversation_DoesNotCreateOne()
    {
        var context = CreateContext();
        context.Request.Headers["X-Conversation-Id"] = "must-not-persist";
        var request = new ChatRequest
        {
            Message = "select an agent",
            Context = new Dictionary<string, object>
            {
                ["agentId"] = "intent-router",
                ["conversationId"] = "also-must-not-persist"
            }
        };

        var agentRequest = AgentEndpointRequestMapper.CreateAgentRequest(
            request,
            context,
            createConversation: false);

        Assert.Equal("intent-router", agentRequest.AgentId);
        Assert.Null(agentRequest.ConversationId);
    }

}
