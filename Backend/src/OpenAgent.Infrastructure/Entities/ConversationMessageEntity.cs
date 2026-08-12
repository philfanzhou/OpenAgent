namespace OpenAgent.Infrastructure.Entities;

internal sealed class ConversationMessageEntity
{
    public required string MessageId { get; init; }
    public required string ConversationId { get; init; }
    public int Sequence { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? IdempotencyKey { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? MetadataJson { get; init; }
}
