using System.Diagnostics;
using System.Collections.Concurrent;
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
            ShouldListenTo = source => source.Name == "OpenAgent.Engine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using ActivitySource source = new("OpenAgent.Engine");
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

        Assert.True(AuditActivityEnrichment.Publish(turn, "sinzoli"));
        AuditActivityEnrichment.PublishContent("我买的手机三天没发货", "您的订单已发货");

        Assert.Equal("sinzoli", activity.GetTagItem("openagent.username"));
        Assert.Equal("conv-001", activity.GetTagItem("openagent.conversation_id"));
        Assert.Equal("cs-bot", activity.GetTagItem("openagent.agent_id"));
        Assert.Equal("我买的手机三天没发货", activity.GetTagItem("langfuse.observation.input"));
        Assert.Equal("您的订单已发货", activity.GetTagItem("langfuse.observation.output"));
    }

    [Fact]
    public void Publish_WithoutActivity_ReturnsFalse()
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
        Assert.False(AuditActivityEnrichment.Publish(turn, "sinzoli"));
        AuditActivityEnrichment.PublishContent("q", "a");
    }

    [Fact]
    public void Publish_WithBareActivity_ReturnsFalse()
    {
        using Activity activity = new Activity("bare").Start();
        TurnContext turn = new()
        {
            TenantId = "tenant-1", UserId = "user-1", ConversationId = "conv-1", TraceId = "trace-1"
        };

        Assert.False(AuditActivityEnrichment.Publish(turn));
        Assert.Null(activity.GetTagItem(AuditAttributeNames.ConversationId));
    }

    [Fact]
    public void StartToolCall_EndToolCall_EmitToolObservationSpan()
    {
        ConcurrentQueue<Activity> stopped = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "OpenAgent.Engine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = span =>
            {
                if (Equals(span.GetTagItem(AuditAttributeNames.TraceId), "trace-tool")) stopped.Enqueue(span);
            },
        };
        ActivitySource.AddActivityListener(listener);

        using ActivitySource source = new("OpenAgent.Engine");
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
        using ToolAuditTrail audit = new(turnContext, "sinli");
        audit.Observe(call);
        Assert.Same(turn, Activity.Current);

        audit.Observe(new AgentStreamEvent { Type = AgentStreamEventType.ToolResult, ToolName = "filesystem.read", ToolCallId = "call-1", Content = "订单数据 3 条" });
        Activity toolSpan = Assert.Single(stopped);

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
        ConcurrentQueue<Activity> stopped = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "OpenAgent.Engine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = span =>
            {
                if (Equals(span.GetTagItem(AuditAttributeNames.TraceId), "trace-tool-truncation")) stopped.Enqueue(span);
            },
        };
        ActivitySource.AddActivityListener(listener);

        TurnContext turnContext = new()
        {
            TenantId = "tenant-1",
            UserId = "u",
            TraceId = "trace-tool-truncation",
            ConversationType = ConversationType.User
        };
        var call = new AgentStreamEvent { Type = AgentStreamEventType.ToolCall, ToolCallId = "big-1", ToolName = "big", ToolArguments = new string('x', 9000) };
        using (ToolAuditTrail audit = new(turnContext, null))
        {
            audit.Observe(call);
        }

        var input = Assert.Single(stopped).GetTagItem("langfuse.observation.input") as string;
        Assert.NotNull(input);
        Assert.True(input.Length < 9000);
        Assert.Contains("审计截断", input);
    }

    [Fact]
    public void Tools_IsolateSameIdBetweenTurnsAndIgnoreOrphansAndBridgeCalls()
    {
        ConcurrentQueue<Activity> stopped = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "OpenAgent.Engine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (Equals(span.GetTagItem(AuditAttributeNames.TraceId), "trace-tool-isolation")) stopped.Enqueue(span);
            }
        };
        ActivitySource.AddActivityListener(listener);
        TurnContext turn = new() { ConversationId = "first", TenantId = "tenant", UserId = "user", TraceId = "trace-tool-isolation" };
        using ToolAuditTrail first = new(turn, null);
        using ToolAuditTrail second = new(turn with { ConversationId = "second" }, null);
        AgentStreamEvent start = new() { Type = AgentStreamEventType.ToolCall, ToolName = "native_tool", ToolCallId = "shared" };
        AgentStreamEvent result = start with { Type = AgentStreamEventType.ToolResult };
        first.Observe(result with { ToolCallId = "orphan" });
        first.Observe(start with { ToolCallId = "update-only", IsToolUpdate = true });
        first.Observe(start with { ToolCallId = "bridge", IsBridgedTool = true });
        first.Observe(start);
        second.Observe(start);
        first.Dispose();
        second.Observe(start with { ToolName = "external_tool", IsToolUpdate = true });
        second.Observe(result with { ToolFailed = true });

        Assert.Collection(stopped,
            span =>
            {
                Assert.Equal("first", span.GetTagItem(AuditAttributeNames.ConversationId));
                Assert.Equal("aborted", span.GetTagItem("openagent.tool.status"));
            },
            span =>
            {
                Assert.Equal("second", span.GetTagItem(AuditAttributeNames.ConversationId));
                Assert.Equal("external_tool", span.DisplayName);
                Assert.Equal("failed", span.GetTagItem("openagent.tool.status"));
            });
    }
}
