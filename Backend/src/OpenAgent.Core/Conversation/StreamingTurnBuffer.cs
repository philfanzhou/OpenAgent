using System.Text;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// 累积本轮已流出到客户端的内容（partial 正文、reasoning、工具调用/结果），
/// 供中断路径持久化与实时一致的时间线；序列分配与落库在 PlatformChatHistory。
/// </summary>
internal sealed class StreamingTurnBuffer
{
    private readonly StringBuilder _partialAssistant = new();
    private readonly StringBuilder _partialReasoning = new();
    private readonly List<ChatMessage> _streamedToolMessages = [];

    internal void AppendPartial(string content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            _partialAssistant.Append(content);
        }
    }

    internal void AppendPartialReasoning(string reasoning)
    {
        if (!string.IsNullOrEmpty(reasoning))
        {
            _partialReasoning.Append(reasoning);
        }
    }

    /// <summary>记录已流出的工具调用（失败/取消时随 partial 一并持久化）。</summary>
    internal void AppendToolCall(string name, string callId, IDictionary<string, object?>? arguments)
    {
        _streamedToolMessages.Add(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, name, arguments)]));
    }

    internal void AppendToolResult(string? callId, string? result)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }
        _streamedToolMessages.Add(new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(callId, result)]));
    }

    internal string PartialAssistant => _partialAssistant.ToString();

    internal string PartialReasoning => _partialReasoning.ToString();

    internal IReadOnlyList<ChatMessage> StreamedToolMessages => _streamedToolMessages;
}
