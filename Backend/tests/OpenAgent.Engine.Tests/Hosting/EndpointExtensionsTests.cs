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
            Context = new Dictionary<string, object>
            {
                ["agentId"] = "body-agent",
                ["conversationId"] = "body-conv",
                ["conversationType"] = "Internal",
                ["conversationOwnerRole"] = "Service",
                ["customKey"] = "custom-value"
            }
        };

        var agentRequest = AgentEndpointRequestMapper.CreateAgentRequest(request, context);

        Assert.Equal("hello", agentRequest.Query);
        Assert.Equal("body-agent", agentRequest.AgentId);
        Assert.Equal("body-conv", agentRequest.ConversationId);
        Assert.Equal(ConversationType.Internal, agentRequest.ConversationType);
        Assert.Equal(ConversationOwnerRole.Service, agentRequest.ConversationOwnerRole);
        Assert.Equal("trace-1", agentRequest.TraceId);
        Assert.NotNull(agentRequest.ExternalContext);
        Assert.Equal("custom-value", agentRequest.ExternalContext!["customKey"]);
        Assert.False(agentRequest.ExternalContext.ContainsKey("agentId"));
        Assert.False(agentRequest.ExternalContext.ContainsKey("conversationId"));
        Assert.False(agentRequest.ExternalContext.ContainsKey("conversationType"));
        Assert.False(agentRequest.ExternalContext.ContainsKey("conversationOwnerRole"));
    }

    [Fact]
    public void CreateAgentRequest_NoChatContext_ExternalContextNull()
    {
        var context = CreateContext(traceId: "trace-2");
        var request = new ChatRequest { Message = "hi" };

        var agentRequest = AgentEndpointRequestMapper.CreateAgentRequest(request, context);

        Assert.Null(agentRequest.AgentId);
        Assert.False(string.IsNullOrWhiteSpace(agentRequest.ConversationId));
        Assert.Equal(ConversationType.User, agentRequest.ConversationType);
        Assert.Equal(ConversationOwnerRole.User, agentRequest.ConversationOwnerRole);
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

}
