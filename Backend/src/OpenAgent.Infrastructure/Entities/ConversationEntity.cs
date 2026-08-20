namespace OpenAgent.Infrastructure.Entities;

internal sealed class ConversationEntity
{
    public required string ConversationId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public string? AgentId { get; set; }
    public string? TraceId { get; set; }
    public int Version { get; set; }
    public int Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public int MessageCount { get; set; }
    public string? Title { get; set; }
    public bool IsDeletedByUser { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string ContextSummariesJson { get; set; } = "[]";
}
