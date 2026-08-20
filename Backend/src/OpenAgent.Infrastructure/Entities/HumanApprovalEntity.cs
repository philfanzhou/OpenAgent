namespace OpenAgent.Infrastructure.Entities;

internal sealed class HumanApprovalEntity
{
    public required string ApprovalId { get; init; }
    public required string TenantId { get; init; }
    public required string ConversationId { get; init; }
    public required string AgentId { get; init; }
    public required string TraceId { get; init; }
    public required string Action { get; init; }
    public int TargetType { get; init; }
    public required string TargetCapability { get; init; }
    public required string RedactedArgumentsJson { get; init; }
    public required string RequestedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public int Status { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
    public required string MafRequestId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string SessionStateJson { get; init; }
    public required string RequesterContextJson { get; init; }
    public int Version { get; set; } = 1;
}
