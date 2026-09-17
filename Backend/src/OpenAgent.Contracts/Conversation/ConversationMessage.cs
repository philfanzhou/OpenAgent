using OpenAgent.Contracts.Requests;

namespace OpenAgent.Contracts.Conversation;

public sealed class ConversationMessage
{
    public required string MessageId { get; init; }
    public required int Sequence { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? IdempotencyKey { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>产生该消息的轮次追溯键（前端 X-Trace-Id）；历史消息可能为空。</summary>
    public string? TraceId { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public IReadOnlyList<string> FileIds { get; init; } = Array.Empty<string>();
    public TokenUsage? TokenUsage { get; init; }
    public string? ModelId { get; init; }
}
