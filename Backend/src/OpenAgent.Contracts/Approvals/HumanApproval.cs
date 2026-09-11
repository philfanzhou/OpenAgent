using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Approvals;

public enum HumanApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public sealed record HumanApprovalRequest
{
    public required string ApprovalId { get; init; }
    public required string TenantId { get; init; }
    public required string ConversationId { get; init; }
    public required string Action { get; init; }
    public required string RedactedArgumentsJson { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public HumanApprovalStatus Status { get; init; } = HumanApprovalStatus.Pending;
}

public sealed record HumanApprovalDecisionRequest
{
    public bool Approved { get; init; }
    public string? Reason { get; init; }
}

public interface IHumanApprovalService
{
    Task<HumanApprovalDecisionResult> DecideAsync(
        string tenantId,
        string approvalId,
        HumanApprovalDecisionRequest decision,
        IAgentUserContext approver,
        CancellationToken cancellationToken = default);
}

public sealed record HumanApprovalDecisionResult
{
    public required HumanApprovalRequest Approval { get; init; }
    public HumanApprovalRequest? NextApproval { get; init; }
}
