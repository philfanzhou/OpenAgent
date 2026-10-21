using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime;

/// <summary>把协议更新转成既有 SSE 事件；工具状态按 ID 合并，避免结果失去名称或参数。</summary>
internal sealed class AcpEventMapper
{
    private readonly Dictionary<string, ToolState> _tools = [];

    internal IEnumerable<AgentStreamEvent> Map(SessionUpdate update)
    {
        switch (update)
        {
            case AgentMessageChunk { Content: TextContent text }:
                yield return new()
                {
                    Type = AgentStreamEventType.Content,
                    Content = text.Text
                };
                break;
            case AgentThoughtChunk { Content: TextContent text }:
                yield return new()
                {
                    Type = AgentStreamEventType.Reasoning,
                    Content = text.Text
                };
                break;
            case ToolCall call:
                ToolState state = new(Name(call.Name ?? call.Title), call.RawInput, call.Content, call.RawOutput);
                _tools[call.ToolCallId] = state;
                yield return Call(call.ToolCallId, state);
                if (call.Status is "completed" or "failed")
                {
                    state.Completed = true;
                    yield return Result(call.ToolCallId, state);
                }
                break;
            case ToolCallUpdate patch:
                bool first = !_tools.TryGetValue(patch.ToolCallId, out ToolState? current);
                current ??= new(Name(patch.Name.Value ?? patch.Title.Value ?? "tool"), null, null, null);
                _tools[patch.ToolCallId] = current;
                string? previousInput = current.Input?.GetRawText();
                string previousName = current.Name;
                if (patch.Name.IsDefined && !patch.Name.IsNull)
                {
                    current.Name = Name(patch.Name.Value!);
                }
                if (patch.RawInput.IsDefined)
                {
                    current.Input = patch.RawInput.IsNull ? null : patch.RawInput.Value;
                }
                if (patch.Content.IsDefined)
                {
                    current.Content = patch.Content.Value;
                }
                if (patch.RawOutput.IsDefined)
                {
                    current.Output = patch.RawOutput.IsNull ? null : patch.RawOutput.Value;
                }
                if (first || previousName != current.Name || (patch.RawInput.IsDefined && previousInput != current.Input?.GetRawText()))
                {
                    yield return Call(patch.ToolCallId, current);
                }
                if (!current.Completed && patch.Status.Value is "completed" or "failed")
                {
                    current.Completed = true;
                    yield return Result(patch.ToolCallId, current);
                }
                break;
            case PlanUpdate plan:
                yield return new()
                {
                    Type = AgentStreamEventType.PlanUpdated,
                    Content = JsonSerializer.Serialize(new
                    {
                        plan = plan.Entries.Select(entry => new { step = entry.Content, status = entry.Status })
                    })
                };
                break;
                // usage_update.used 是上下文占用量，不是本轮计费 token；不得伪造 TokenUsage。
        }
    }

    // OpenCode 为 MCP 工具增加服务器前缀；平台事件沿用已有工具名，保证展示/统计兼容。
    private static string Name(string name) => name.StartsWith("openagent_", StringComparison.Ordinal)
        ? name["openagent_".Length..]
        : name;

    private static string Output(JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("output", out JsonElement text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }
        return output.ValueKind == JsonValueKind.String ? output.GetString() ?? string.Empty : output.GetRawText();
    }

    private static AgentStreamEvent Call(string id, ToolState tool) => new()
    {
        Type = AgentStreamEventType.ToolCall,
        ToolCallId = id,
        ToolName = tool.Name,
        ToolArguments = tool.Input is { ValueKind: JsonValueKind.Object } input
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(input)
            : new Dictionary<string, object?>()
    };

    private static AgentStreamEvent Result(string id, ToolState tool) => new()
    {
        Type = AgentStreamEventType.ToolResult,
        ToolCallId = id,
        ToolName = tool.Name,
        Content = tool.Output is { } output
            ? Output(output)
            : string.Join("\n", (tool.Content ?? []).Select(content => content switch
            {
                ToolContent { Content: TextContent text } => text.Text,
                ToolDiff diff => diff.NewText,
                ToolTerminal terminal => "Terminal: " + terminal.TerminalId,
                _ => string.Empty
            }))
    };

    private sealed class ToolState(string name, JsonElement? input, ToolCallContent[]? content, JsonElement? output)
    {
        internal string Name = name;
        internal JsonElement? Input = input;
        internal ToolCallContent[]? Content = content;
        internal JsonElement? Output = output;
        internal bool Completed;
    }
}
