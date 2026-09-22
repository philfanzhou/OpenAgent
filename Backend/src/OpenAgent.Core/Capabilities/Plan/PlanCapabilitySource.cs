using System.Text.Json;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities.Plan;

/// <summary>
/// update_plan：任务计划工具（对标 Claude Code TodoWrite / Codex update_plan）。
/// 模型每次全量重发步骤列表（这是主流实现的标准做法），因此无需跨调用状态；
/// 计划的最新内容作为工具结果落进会话时间线（刷新即可回放），SSE 侧由执行器
/// 在工具结果处附加 PlanUpdated 事件供前端渲染。
/// </summary>
internal sealed class PlanCapabilitySource : ICapabilitySource
{
    private const string Name = "update_plan";
    private const string Description =
        "Record or update the task plan for the current run. "
        + "Create a plan BEFORE starting any task that needs 3 or more steps, and update it whenever a step starts, "
        + "finishes, or the approach changes. Send the FULL list of steps every call (there is no partial update). "
        + "At most one step may be in_progress at a time; mark steps completed as soon as they finish. "
        + "Skip this tool for simple one-step or conversational requests.";
    private const string ParametersJsonSchema = """
        {
          "type": "object",
          "properties": {
            "plan": {
              "type": "array",
              "minItems": 1,
              "maxItems": 20,
              "items": {
                "type": "object",
                "properties": {
                  "step": {
                    "type": "string",
                    "description": "Short imperative description of the step"
                  },
                  "status": {
                    "type": "string",
                    "enum": ["pending", "in_progress", "completed"],
                    "description": "Step status; at most one step may be in_progress"
                  }
                },
                "required": ["step", "status"],
                "additionalProperties": false
              }
            }
          },
          "required": ["plan"],
          "additionalProperties": false
        }
        """;

    private static readonly string[] Statuses = ["pending", "in_progress", "completed"];

    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CapabilityDefinition> definitions =
        [
            new CapabilityDefinition(
                Name,
                Description,
                ParametersJsonSchema,
                AgentResourceType.Tool,
                Name,
                UpdateAsync,
                Concurrency: ToolConcurrency.ReadOnly)
        ];
        return Task.FromResult(definitions);
    }

    private static Task<ToolResult> UpdateAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("plan", out object? planValue) || planValue is null)
        {
            return Task.FromResult(ToolResult.Error(
                "'plan' is a required argument; provide the full list of steps.",
                "invalid_arguments",
                hint: "Example: [{\"step\":\"...\",\"status\":\"pending\"}]."));
        }

        List<(string Step, string Status)> steps = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                JsonSerializer.SerializeToUtf8Bytes(planValue));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(ToolResult.Error(
                    "'plan' must be an array of step objects.",
                    "invalid_arguments",
                    hint: "Example: [{\"step\":\"...\",\"status\":\"pending\"}]."));
            }
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("step", out JsonElement step)
                    || step.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("status", out JsonElement status)
                    || status.ValueKind != JsonValueKind.String)
                {
                    return Task.FromResult(ToolResult.Error(
                        "Each plan item must be an object with string 'step' and string 'status'.",
                        "invalid_arguments",
                        hint: "Allowed status values: pending, in_progress, completed."));
                }
                string stepText = step.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(stepText))
                {
                    return Task.FromResult(ToolResult.Error(
                        "Plan steps must not be empty.",
                        "invalid_arguments"));
                }
                steps.Add((stepText, status.GetString() ?? string.Empty));
            }
        }
        catch (JsonException)
        {
            return Task.FromResult(ToolResult.Error(
                "'plan' must be an array of step objects.",
                "invalid_arguments",
                hint: "Example: [{\"step\":\"...\",\"status\":\"pending\"}]."));
        }

        if (steps.Count == 0)
        {
            return Task.FromResult(ToolResult.Error(
                "'plan' must contain at least one step.",
                "invalid_arguments"));
        }
        foreach ((string step, string status) in steps)
        {
            if (!Statuses.Contains(status, StringComparer.Ordinal))
            {
                return Task.FromResult(ToolResult.Error(
                    $"Step '{step}' has invalid status '{status}'.",
                    "invalid_arguments",
                    hint: "Allowed status values: pending, in_progress, completed."));
            }
        }
        int inProgress = steps.Count(item => item.Status == "in_progress");
        if (inProgress > 1)
        {
            return Task.FromResult(ToolResult.Error(
                $"At most one step may be in_progress at a time (found {inProgress}).",
                "invalid_arguments",
                hint: "Finish or revert the current step before starting the next one."));
        }

        // 结果即最新计划的快照：进入会话时间线（刷新可回放），SSE 侧由执行器
        // 附加 PlanUpdated 事件。completed/all 的计数帮助模型自检进度。
        return Task.FromResult(ToolResult.Text(JsonSerializer.Serialize(new
        {
            plan = steps.Select(item => new { item.Step, item.Status }).ToArray(),
            completed = steps.Count(item => item.Status == "completed"),
            total = steps.Count
        })));
    }
}
