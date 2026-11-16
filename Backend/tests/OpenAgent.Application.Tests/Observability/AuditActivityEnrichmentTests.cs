using System.Diagnostics;
using OpenAgent.Application.Execution;
using OpenAgent.Application.Observability;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;
using Xunit;

namespace OpenAgent.Application.Tests.Observability;

/// <summary>
/// 审计 Activity 的标签契约：身份 + 登录名 + 对话内容必须全部落在同一个
/// Activity 上——审计网关按这些属性名掩码内容、提升 agent、定位用户。
/// </summary>
public class AuditActivityEnrichmentTests
{
    [Fact]
    public void Publish_WithUsernameAndContent_AllTagsLandOnOneActivity()
    {
        List<Activity> stopped = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using ActivitySource source = new("OpenAgent.Test");
        using Activity? activity = source.StartActivity("POST /api/v1/agent/chat/stream");
        Assert.NotNull(activity);

        TurnContext turn = new()
        {
            TenantId = "tenant-1",
            UserId = "8b95d904-12fb-42cc-b31e-88ef997329a7",
            ConversationId = "conv-001",
            TraceId = "trace-001",
            AgentId = "cs-bot",
            ConversationType = ConversationType.User
        };

        AuditActivityEnrichment.Publish(turn, "sinzoli");
        AuditActivityEnrichment.PublishContent("我买的手机三天没发货", "您的订单已发货");

        Assert.Equal("sinzoli", activity.GetTagItem("openagent.username"));
        Assert.Equal("conv-001", activity.GetTagItem("openagent.conversation_id"));
        Assert.Equal("cs-bot", activity.GetTagItem("openagent.agent_id"));
        Assert.Equal("我买的手机三天没发货", activity.GetTagItem("langfuse.observation.input"));
        Assert.Equal("您的订单已发货", activity.GetTagItem("langfuse.observation.output"));
    }

    [Fact]
    public void Publish_WithoutActivity_DoesNotThrow()
    {
        // 无监听器时 Activity.Current 为 null——静默返回，不拖垮聊天。
        TurnContext turn = new()
        {
            TenantId = "tenant-1",
            UserId = "u-1",
            TraceId = "t-1",
            AgentId = "cs-bot",
            ConversationType = ConversationType.User
        };
        AuditActivityEnrichment.Publish(turn, "sinzoli");
        AuditActivityEnrichment.PublishContent("q", "a");
    }
}
