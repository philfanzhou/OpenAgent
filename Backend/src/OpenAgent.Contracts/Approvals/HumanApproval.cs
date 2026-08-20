using System.Text.Json.Serialization;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Approvals;

public enum HumanApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Withdrawn = 4,
    Preparing = 5
}

public sealed class HumanApprovalRequest
{
    public required string ApprovalId { get; init; }
    public required string TenantId { get; init; }
    public required string ConversationId { get; init; }
    public required string AgentId { get; init; }
    public required string Action { get; init; }
    public required AgentResourceType TargetType { get; init; }
    public required string TargetCapability { get; init; }
    public required string RedactedArgumentsJson { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required HumanApprovalStatus Status { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecisionReason { get; init; }
}

public sealed class HumanApprovalRecord
{
    public required string ApprovalId { get; init; }
    public required string TenantId { get; init; }
    public required string ConversationId { get; init; }
    public required string AgentId { get; init; }
    public required string TraceId { get; init; }
    public required string Action { get; init; }
    public required AgentResourceType TargetType { get; init; }
    public required string TargetCapability { get; init; }
    public required string RedactedArgumentsJson { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required HumanApprovalStatus Status { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecisionReason { get; init; }
    public required string MafRequestId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public int Version { get; init; } = 1;

    [JsonIgnore]
    public string SessionStateJson { get; init; } = string.Empty;

    [JsonIgnore]
    public string RequesterContextJson { get; init; } = string.Empty;

    public HumanApprovalRequest ToRequest() => new()
    {
        ApprovalId = ApprovalId,
        TenantId = TenantId,
        ConversationId = ConversationId,
        AgentId = AgentId,
        Action = Action,
        TargetType = TargetType,
        TargetCapability = TargetCapability,
        RedactedArgumentsJson = RedactedArgumentsJson,
        RequestedBy = RequestedBy,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        Status = Status,
        DecidedBy = DecidedBy,
        DecidedAt = DecidedAt,
        DecisionReason = DecisionReason
    };
}

public sealed class HumanApprovalDecisionRequest
{
    public bool Approved { get; init; }
    public string? Reason { get; init; }
}

public sealed class HumanApprovalDecisionResult
{
    public required HumanApprovalRequest Approval { get; init; }
    public AgentResponse? Response { get; init; }
}

public interface IHumanApprovalStore
{
    Task<bool> CreateAsync(
        HumanApprovalRecord approval,
        CancellationToken cancellationToken = default);

    Task<HumanApprovalRecord?> GetAsync(
        string tenantId,
        string approvalId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HumanApprovalRecord>> ListAsync(
        string tenantId,
        HumanApprovalStatus? status = null,
        CancellationToken cancellationToken = default);

    Task<HumanApprovalRecord?> TryTransitionAsync(
        string tenantId,
        string approvalId,
        HumanApprovalStatus expectedStatus,
        HumanApprovalStatus newStatus,
        string actorId,
        string? reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HumanApprovalRecord>> ExpirePendingAsync(
        DateTimeOffset expiresAtOrBefore,
        string? tenantId = null,
        CancellationToken cancellationToken = default);
}

public interface IHumanApprovalService
{
    Task<HumanApprovalRequest?> GetAsync(
        string tenantId,
        string approvalId,
        IAgentUserContext user,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HumanApprovalRequest>> ListPendingAsync(
        string tenantId,
        IAgentUserContext user,
        CancellationToken cancellationToken = default);

    Task<HumanApprovalDecisionResult> DecideAsync(
        string tenantId,
        string approvalId,
        HumanApprovalDecisionRequest decision,
        IAgentUserContext approver,
        CancellationToken cancellationToken = default);

    Task<HumanApprovalRequest> WithdrawAsync(
        string tenantId,
        string approvalId,
        IAgentUserContext requester,
        CancellationToken cancellationToken = default);

    Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default);
}
