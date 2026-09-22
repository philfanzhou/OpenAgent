using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Plan;
using OpenAgent.Core.Tests.Runtime;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class PlanCapabilitySourceTests
{
    [Fact]
    public async Task UpdateAsync_ValidPlan_ReturnsSnapshotWithCounts()
    {
        CapabilityDefinition definition = DiscoverDefinition();

        ToolResult result = await definition.Invoke(
            new Dictionary<string, object?>
            {
                ["plan"] = JsonSerializer.Deserialize<JsonElement>(
                    """[{"step":"collect","status":"completed"},{"step":"analyze","status":"in_progress"},{"step":"report","status":"pending"}]""")
            },
            CancellationToken.None);

        Assert.False(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal(3, document.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("completed").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("plan").GetArrayLength());
    }

    [Fact]
    public async Task UpdateAsync_TwoInProgressSteps_ReturnsActionableError()
    {
        CapabilityDefinition definition = DiscoverDefinition();

        ToolResult result = await definition.Invoke(
            new Dictionary<string, object?>
            {
                ["plan"] = JsonSerializer.Deserialize<JsonElement>(
                    """[{"step":"a","status":"in_progress"},{"step":"b","status":"in_progress"}]""")
            },
            CancellationToken.None);

        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            "At most one step",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"step":"a","status":"done"}]""")]
    [InlineData("""[{"step":"a"}]""")]
    [InlineData("""[]""")]
    [InlineData("""{"step":"a","status":"pending"}""")]
    public async Task UpdateAsync_MalformedPlan_ReturnsActionableError(string rawPlan)
    {
        CapabilityDefinition definition = DiscoverDefinition();

        ToolResult result = await definition.Invoke(
            new Dictionary<string, object?> { ["plan"] = JsonSerializer.Deserialize<JsonElement>(rawPlan) },
            CancellationToken.None);

        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpdateAsync_MissingPlan_ReturnsRequiredArgumentError()
    {
        CapabilityDefinition definition = DiscoverDefinition();

        ToolResult result = await definition.Invoke(
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExecuteStreamingAsync_UpdatePlanCall_EmitsPlanUpdatedAfterToolResult()
    {
        // update_plan 的工具结果之后必须紧跟 PlanUpdated 事件（Content 为计划快照），
        // 前端据此渲染任务清单，无需解析通用工具结果。
        var provider = new SequenceChatProvider(
        [
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "update_plan", new Dictionary<string, object?>
                {
                    ["plan"] = new object[]
                    {
                        new Dictionary<string, object?> { ["step"] = "collect", ["status"] = "completed" },
                        new Dictionary<string, object?> { ["step"] = "report", ["status"] = "in_progress" }
                    }
                })])],
            [new ChatResponseUpdate(ChatRole.Assistant, "计划已更新，继续执行")]
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider, maxTurns: 4);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("plan-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        int toolResultIndex = events.FindIndex(item =>
            item.Type == AgentStreamEventType.ToolResult && item.ToolCallId == "call-1");
        Assert.True(toolResultIndex >= 0, "tool result event must be emitted");
        AgentStreamEvent? planEvent = events
            .Where(item => item.Type == AgentStreamEventType.PlanUpdated)
            .SingleOrDefault();
        Assert.NotNull(planEvent);
        Assert.True(events.IndexOf(planEvent!) > toolResultIndex, "plan event must follow the tool result");
        using JsonDocument document = JsonDocument.Parse(planEvent!.Content!);
        Assert.Equal(2, document.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("completed").GetInt32());
    }

    private static CapabilityDefinition DiscoverDefinition()
    {
        IReadOnlyList<CapabilityDefinition> definitions = new PlanCapabilitySource().DiscoverAsync(
            "agent-1",
            new AgentConfig(),
            User,
            CancellationToken.None).GetAwaiter().GetResult();
        return Assert.Single(definitions);
    }

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = "tenant-1"
    };

    private static AgentRequest CreateRequest(string conversationId) => new()
    {
        Query = "organize the task",
        AgentId = "test-agent",
        LlmProfileId = "test-model",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}"
    };
}
