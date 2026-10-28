namespace OpenAgent.Infrastructure.Entities;

internal sealed class LlmInteractionEntity
{
    public required string InteractionId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public string? ConversationId { get; init; }
    public required string TraceId { get; init; }
    public string? AgentId { get; init; }
    public int Source { get; init; }
    public string? Provider { get; init; }
    public string? ApiFormat { get; init; }
    public required string ModelId { get; init; }
    public bool Streamed { get; init; }
    public int CallIndex { get; init; }
    public string? RequestJson { get; init; }
    public string? ResponseJson { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
    public int? CachedInputTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int Status { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public int DurationMs { get; init; }
}
