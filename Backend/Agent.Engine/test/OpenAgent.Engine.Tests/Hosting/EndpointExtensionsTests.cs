using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class EndpointExtensionsTests
{
    [Fact]
    public void CreateAgentRequest_ContextPopulated_ReadsIdsFromScopedContext()
    {
        // Arrange
        var requestContext = new StubAgentRequestContext
        {
            AgentId = "agent-from-context",
            ConversationId = "conv-from-context",
            TraceId = "trace-from-context"
        };
        var request = new ChatRequest
        {
            Message = "hello",
            Context = new Dictionary<string, object>
            {
                ["agentId"] = "body-agent",
                ["conversationId"] = "body-conv",
                ["traceId"] = "body-trace",
                ["customKey"] = "custom-value"
            }
        };

        // Act
        var agentRequest = EndpointExtensions.CreateAgentRequest(request, requestContext);

        // Assert
        Assert.Equal("hello", agentRequest.Query);
        Assert.Equal("agent-from-context", agentRequest.AgentId);
        Assert.Equal("conv-from-context", agentRequest.ConversationId);
        Assert.Equal("trace-from-context", agentRequest.TraceId);
        Assert.NotNull(agentRequest.ExternalContext);
        Assert.Equal("custom-value", agentRequest.ExternalContext!["customKey"]);
        Assert.Equal("true", agentRequest.ExternalContext["EmitTelemetryEvents"]);
        Assert.False(agentRequest.ExternalContext.ContainsKey("agentId"));
        Assert.False(agentRequest.ExternalContext.ContainsKey("conversationId"));
        Assert.False(agentRequest.ExternalContext.ContainsKey("traceId"));
    }

    [Fact]
    public void CreateAgentRequest_NullChatContext_EmitsTelemetryFlagOnly()
    {
        // Arrange
        var requestContext = new StubAgentRequestContext
        {
            TraceId = "trace-1"
        };
        var request = new ChatRequest
        {
            Message = "hi"
        };

        // Act
        var agentRequest = EndpointExtensions.CreateAgentRequest(request, requestContext);

        // Assert
        Assert.Null(agentRequest.AgentId);
        Assert.Null(agentRequest.ConversationId);
        Assert.Equal("trace-1", agentRequest.TraceId);
        Assert.NotNull(agentRequest.ExternalContext);
        Assert.Single(agentRequest.ExternalContext!);
        Assert.Equal("true", agentRequest.ExternalContext["EmitTelemetryEvents"]);
    }

    private sealed class StubAgentRequestContext : IAgentRequestContext
    {
        public string? AgentId { get; init; }

        public string? ConversationId { get; init; }

        public string UserId { get; init; } = "anonymous";

        public string? TenantId { get; init; }

        public string TraceId { get; init; } = string.Empty;

        public IAgentUserContext UserContext { get; init; } = new AgentUserContext
        {
            UserId = "anonymous"
        };
    }
}
