using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

/// <summary>
/// 走查断言：TurnContext 到三套下游 scope 的派生必须字段完整、无丢失，
/// 新增横切字段落到 TurnContext 后只需扩展这些投影。
/// </summary>
public class TurnContextProjectionsTests
{
    [Fact]
    public void ToFileAssetScope_MapsIdentityCoordinates()
    {
        TurnContext turn = TurnContexts.Create(
            tenantId: "tenant-a",
            userId: "user-a",
            conversationId: "conversation-a");

        FileAssetScope projected = turn.ToFileAssetScope();
        Assert.Equal("tenant-a", projected.TenantId);
        Assert.Equal("user-a", projected.UserId);
        Assert.Equal("conversation-a", projected.ConversationId);
    }

    [Theory]
    [InlineData(LlmInteractionSource.AgentTurn)]
    [InlineData(LlmInteractionSource.Compaction)]
    public void ToCapture_MapsAllCoordinates(LlmInteractionSource source)
    {
        TurnContext turn = TurnContexts.Create(
            tenantId: "tenant-a",
            userId: "user-a",
            conversationId: "conversation-a",
            traceId: "trace-a",
            agentId: "agent-a");

        LlmInteractionCapture capture = turn.ToCapture(source);

        Assert.Equal("tenant-a", capture.TenantId);
        Assert.Equal("user-a", capture.UserId);
        Assert.Equal("conversation-a", capture.ConversationId);
        Assert.Equal("trace-a", capture.TraceId);
        Assert.Equal("agent-a", capture.AgentId);
        Assert.Equal(source, capture.Source);
    }

    [Fact]
    public void ToConversationContext_MapsAllCoordinates()
    {
        TurnContext turn = TurnContexts.Create(
            tenantId: "tenant-a",
            userId: "user-a",
            conversationId: "conversation-a",
            traceId: "trace-a",
            agentId: "agent-a",
            conversationType: ConversationType.Channel);

        ConversationContext context = turn.ToConversationContext();

        Assert.Equal("conversation-a", context.ConversationId);
        Assert.Equal("tenant-a", context.TenantId);
        Assert.Equal("user-a", context.UserId);
        Assert.Equal("agent-a", context.AgentId);
        Assert.Equal("trace-a", context.TraceId);
        Assert.Equal(ConversationType.Channel, context.Type);
        Assert.True(context.IsValid);
    }

    [Fact]
    public void ToConversationContext_NullType_FallsBackToUser()
    {
        TurnContext turn = TurnContexts.Create(conversationType: null);

        Assert.Equal(ConversationType.User, turn.ToConversationContext().Type);
    }
}
