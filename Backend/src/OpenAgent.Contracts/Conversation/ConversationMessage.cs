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
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public IReadOnlyList<string> FileIds { get; init; } = Array.Empty<string>();
}
