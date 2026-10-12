using System.Text.Json;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Core.Mapping;

internal sealed class AcpEventMapper
{
    private readonly Dictionary<string, AgentStreamEvent> _calls = [];
    private readonly HashSet<string> _finished = [];

    internal AgentStreamEvent? Map(JsonElement update)
    {
        string? kind = ReadString(update, "sessionUpdate");
        if (kind is "agent_message_chunk" or "agent_thought_chunk")
        {
            JsonElement content = update.GetProperty("content");
            if (ReadString(content, "type") != "text")
                return null;
            return new AgentStreamEvent
            {
                Type = kind == "agent_message_chunk" ? AgentStreamEventType.Content : AgentStreamEventType.Reasoning,
                Content = ReadString(content, "text")
            };
        }
        if (kind is "tool_call" or "tool_call_update")
        {
            string id = ReadString(update, "toolCallId") ?? throw new InvalidOperationException("An ACP tool call is missing its identifier.");
            _calls.TryGetValue(id, out AgentStreamEvent? previous);
            string? name = ReadString(update, "name") ?? previous?.ToolName ?? ReadString(update, "title");
            object? input = update.TryGetProperty("rawInput", out JsonElement raw) && raw.ValueKind != JsonValueKind.Null
                ? raw.Clone() : previous?.ToolArguments;
            var call = new AgentStreamEvent { Type = AgentStreamEventType.ToolCall, ToolCallId = id, ToolName = name, ToolArguments = input };
            string? status = ReadString(update, "status");
            if (status is "completed" or "failed")
            {
                _calls[id] = call;
                if (!_finished.Add(id))
                    return null;
                string result = update.TryGetProperty("rawOutput", out JsonElement output) && output.ValueKind != JsonValueKind.Null
                    ? output.ValueKind == JsonValueKind.String ? output.GetString()! : output.GetRawText()
                    : RenderContents(update);
                return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolResult,
                    ToolCallId = id,
                    ToolName = name,
                    Content = status == "failed" && result.Length == 0 ? "Tool execution failed." : result
                };
            }
            _calls[id] = call;
            return previous == null || input != previous.ToolArguments ? call : null;
        }
        if (kind == "plan" && update.TryGetProperty("entries", out JsonElement entries))
        {
            return new AgentStreamEvent
            {
                Type = AgentStreamEventType.PlanUpdated,
                Content = JsonSerializer.Serialize(new
                {
                    plan = entries.EnumerateArray().Select(entry => new { step = ReadString(entry, "content"), status = ReadString(entry, "status") }).ToArray()
                })
            };
        }
        return null;
    }

    private static string RenderContents(JsonElement update)
    {
        if (!update.TryGetProperty("content", out JsonElement contents))
            return "";
        return string.Join("\n", contents.EnumerateArray().Where(item => ReadString(item, "type") == "content")
            .Select(item => item.GetProperty("content"))
            .Where(item => ReadString(item, "type") == "text").Select(item => ReadString(item, "text")));
    }

    internal static string? ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() : null;
}
