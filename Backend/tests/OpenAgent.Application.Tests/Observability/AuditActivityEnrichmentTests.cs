using System.Diagnostics;
using OpenAgent.Application.Execution;
using OpenAgent.Application.Observability;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
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

    [Fact]
    public void StartToolCall_EndToolCall_EmitToolObservationSpan()
    {
        List<Activity> stopped = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using ActivitySource source = new("OpenAgent");
        using Activity? turn = source.StartActivity("POST /api/v1/agent/chat/stream");
        Assert.NotNull(turn);

        TurnContext turnContext = new()
        {
            TenantId = "tenant-1",
            UserId = "8b95d904-12fb",
            ConversationId = "conv-tool",
            TraceId = "trace-tool",
            AgentId = "cs-bot",
            ConversationType = ConversationType.User
        };
        var call = new AgentStreamEvent { Type = AgentStreamEventType.ToolCall, ToolName = "filesystem.read", ToolCallId = "call-1", ToolArguments = new { path = "/data/orders.xlsx" } };
        using var toolSpan = AuditActivityEnrichment.StartToolCall(turnContext, "sinli", call);
        Assert.NotNull(toolSpan);

        AuditActivityEnrichment.EndToolCall(new AgentStreamEvent { Type = AgentStreamEventType.ToolResult, ToolName = "filesystem.read", ToolCallId = "call-1", Content = "订单数据 3 条" });

        Assert.Equal("filesystem.read", toolSpan.OperationName);
        Assert.Equal("sinli", toolSpan.GetTagItem("openagent.username"));
        Assert.Equal("cs-bot", toolSpan.GetTagItem("openagent.agent_id"));
        Assert.Equal("tool", toolSpan.GetTagItem("langfuse.observation.type"));
        Assert.Contains("orders.xlsx", toolSpan.GetTagItem("langfuse.observation.input") as string);
        Assert.Contains("订单数据 3 条", toolSpan.GetTagItem("langfuse.observation.output") as string);
        Assert.Equal("completed", toolSpan.GetTagItem("openagent.tool.status"));
    }

    [Fact]
    public void ToolCallArguments_LongerThanLimit_AreTruncated()
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
        using Activity? turn = source.StartActivity("t");
        TurnContext turnContext = new()
        {
            TenantId = "tenant-1",
            UserId = "u",
            TraceId = "t",
            ConversationType = ConversationType.User
        };
        var call = new AgentStreamEvent { Type = AgentStreamEventType.ToolCall, ToolName = "big", ToolArguments = new string('x', 9000) };
        using var toolSpan = AuditActivityEnrichment.StartToolCall(turnContext, null, call);

        var input = toolSpan.GetTagItem("langfuse.observation.input") as string;
        Assert.NotNull(input);
        Assert.True(input.Length < 9000);
        Assert.Contains("审计截断", input);
    }
}
