using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime;

/// <summary>把协议更新转成既有 SSE 事件；工具状态按 ID 合并，避免结果失去名称或参数。</summary>
/// <param name="toolNamePrefix">
/// harness 给 MCP 工具名加的服务器前缀（如 OpenCode 的 "openagent_"），映射前剥掉以沿用平台工具名；null 表示不剥。
/// </param>
internal sealed class AcpEventMapper(string? toolNamePrefix = null)
{
    private readonly Dictionary<string, ToolState> _tools = [];

    /// <summary>
    /// 最近一次 usage_update：Used 为本轮结束时 harness 的上下文占用，Size 为其对端窗口上限。
    /// 是占用量而非逐项计费值；映射进 TokenUsage 时只填 total/prompt，不拆分 completion。
    /// </summary>
    internal (long Used, long Size)? LastUsage { get; private set; }

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
            case UsageUpdate usage:
                LastUsage = (usage.Used, usage.Size);
                break;
        }
    }

    private string Name(string name) => toolNamePrefix is { } prefix && name.StartsWith(prefix, StringComparison.Ordinal)
        ? name[prefix.Length..]
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
