using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Configuration;
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

    [Theory]
    [InlineData("message", false, true)]
    [InlineData("conversation", true, false)]
    public void CreateAgentRequest_ModelOverride_MapsRequestedScope(
        string scope,
        bool updatesConversation,
        bool isMessageOverride)
    {
        var request = new ChatRequest
        {
            Message = "hello",
            Context = new Dictionary<string, object>
            {
                ["modelScope"] = scope,
                ["modelProvider"] = "provider-1",
                ["modelId"] = "model-1",
                ["custom"] = "value"
            }
        };

        AgentRequest result = AgentEndpointRequestMapper.CreateAgentRequest(
            request,
            CreateContext());
        LlmModelSelection selection = Assert.IsType<LlmModelSelection>(
            isMessageOverride ? result.MessageModelOverride : result.ConversationModelOverride);

        Assert.Equal("provider-1", selection.Provider);
        Assert.Equal("model-1", selection.ModelId);
        Assert.Equal(updatesConversation, result.UpdateConversationModelOverride);
        Assert.Equal("value", result.ExternalContext?["custom"]);
        Assert.False(result.ExternalContext!.ContainsKey("modelProvider"));
    }

    [Fact]
    public void CreateAgentRequest_ConversationScopeWithoutModel_MapsClearOverride()
    {
        var request = new ChatRequest
        {
            Message = "hello",
            Context = new Dictionary<string, object> { ["modelScope"] = "conversation" }
        };

        AgentRequest result = AgentEndpointRequestMapper.CreateAgentRequest(
            request,
            CreateContext());

        Assert.True(result.UpdateConversationModelOverride);
        Assert.Null(result.ConversationModelOverride);
    }

    [Theory]
    [InlineData("message", null, null)]
    [InlineData("message", "provider-1", null)]
    [InlineData("message", null, "model-1")]
    [InlineData("conversation", "provider-1", null)]
    [InlineData("conversation", null, "model-1")]
    public void CreateAgentRequest_IncompleteModelOverride_IsRejected(
        string scope,
        string? provider,
        string? modelId)
    {
        var modelContext = new Dictionary<string, object> { ["modelScope"] = scope };
        if (provider != null)
        {
            modelContext["modelProvider"] = provider;
        }
        if (modelId != null)
        {
            modelContext["modelId"] = modelId;
        }
        var request = new ChatRequest
        {
            Message = "hello",
            Context = modelContext
        };

        AgentException exception = Assert.Throws<AgentException>(() =>
            AgentEndpointRequestMapper.CreateAgentRequest(request, CreateContext()));

        Assert.Equal(AgentErrorCode.InvalidRequest, exception.ErrorCode);
    }

}
