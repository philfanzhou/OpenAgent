using System.Collections.Concurrent;
using OpenAgent.Contracts.Approvals;

namespace OpenAgent.Core.Approvals;

internal sealed class InMemoryHumanApprovalStore : IHumanApprovalStore
{
    private readonly ConcurrentDictionary<string, HumanApprovalRecord> _approvals =
        new(StringComparer.Ordinal);

    public Task<bool> CreateAsync(
        HumanApprovalRecord approval,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_approvals.TryAdd(approval.ApprovalId, approval));

    public Task<HumanApprovalRecord?> GetAsync(
        string tenantId,
        string approvalId,
        CancellationToken cancellationToken = default)
    {
        HumanApprovalRecord? approval = _approvals.GetValueOrDefault(approvalId);
        return Task.FromResult(
            approval != null
            && string.Equals(approval.TenantId, tenantId, StringComparison.Ordinal)
                ? approval
                : null);
    }

    public Task<IReadOnlyList<HumanApprovalRecord>> ListAsync(
        string tenantId,
        HumanApprovalStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HumanApprovalRecord> approvals = _approvals.Values
            .Where(approval => string.Equals(
                approval.TenantId,
                tenantId,
                StringComparison.Ordinal))
            .Where(approval => status == null || approval.Status == status)
            .OrderBy(approval => approval.ExpiresAt)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(approvals);
    }

    public Task<HumanApprovalRecord?> TryTransitionAsync(
        string tenantId,
        string approvalId,
        HumanApprovalStatus expectedStatus,
        HumanApprovalStatus newStatus,
        string actorId,
        string? reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default)
    {
        while (_approvals.TryGetValue(approvalId, out HumanApprovalRecord? current))
        {
            if (!string.Equals(current.TenantId, tenantId, StringComparison.Ordinal)
                || current.Status != expectedStatus
                || expectedStatus is HumanApprovalStatus.Pending or HumanApprovalStatus.Preparing
                    && newStatus != HumanApprovalStatus.Expired
                    && current.ExpiresAt <= decidedAt)
            {
                return Task.FromResult<HumanApprovalRecord?>(null);
            }

            HumanApprovalRecord updated = CopyWithDecision(
                current,
                newStatus,
                actorId,
                reason,
                decidedAt);
            if (_approvals.TryUpdate(approvalId, updated, current))
            {
                return Task.FromResult<HumanApprovalRecord?>(updated);
            }
        }

        return Task.FromResult<HumanApprovalRecord?>(null);
    }

    public async Task<IReadOnlyList<HumanApprovalRecord>> ExpirePendingAsync(
        DateTimeOffset expiresAtOrBefore,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        List<HumanApprovalRecord> expired = [];
        HumanApprovalRecord[] candidates = _approvals.Values
            .Where(approval => approval.Status is
                HumanApprovalStatus.Pending or HumanApprovalStatus.Preparing)
            .Where(approval => approval.ExpiresAt <= expiresAtOrBefore)
            .Where(approval => tenantId == null || string.Equals(
                approval.TenantId,
                tenantId,
                StringComparison.Ordinal))
            .ToArray();
        foreach (HumanApprovalRecord candidate in candidates)
        {
            HumanApprovalRecord? transitioned = await TryTransitionAsync(
                candidate.TenantId,
                candidate.ApprovalId,
                candidate.Status,
                HumanApprovalStatus.Expired,
                "system",
                "Approval request expired.",
                expiresAtOrBefore,
                cancellationToken).ConfigureAwait(false);
            if (transitioned != null)
            {
                expired.Add(transitioned);
            }
        }
        return expired.AsReadOnly();
    }

    private static HumanApprovalRecord CopyWithDecision(
        HumanApprovalRecord source,
        HumanApprovalStatus status,
        string actorId,
        string? reason,
        DateTimeOffset decidedAt) => new()
        {
            ApprovalId = source.ApprovalId,
            TenantId = source.TenantId,
            ConversationId = source.ConversationId,
            AgentId = source.AgentId,
            TraceId = source.TraceId,
            Action = source.Action,
            TargetType = source.TargetType,
            TargetCapability = source.TargetCapability,
            RedactedArgumentsJson = source.RedactedArgumentsJson,
            RequestedBy = source.RequestedBy,
            CreatedAt = source.CreatedAt,
            ExpiresAt = source.ExpiresAt,
            Status = status,
            DecidedBy = status == HumanApprovalStatus.Pending ? null : actorId,
            DecidedAt = status == HumanApprovalStatus.Pending ? null : decidedAt,
            DecisionReason = status == HumanApprovalStatus.Pending ? null : reason,
            MafRequestId = source.MafRequestId,
            ToolCallId = source.ToolCallId,
            ToolName = source.ToolName,
            SessionStateJson = source.SessionStateJson,
            RequesterContextJson = source.RequesterContextJson,
            Version = source.Version + 1
        };
}
