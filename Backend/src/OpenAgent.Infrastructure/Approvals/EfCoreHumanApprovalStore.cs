using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Contracts.Security;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure.Approvals;

internal sealed class EfCoreHumanApprovalStore(
    IDbContextFactory<OpenAgentDbContext> contexts) : IHumanApprovalStore
{
    public async Task<bool> CreateAsync(
        HumanApprovalRecord approval,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.HumanApprovals.Add(ToEntity(approval));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    public async Task<HumanApprovalRecord?> GetAsync(
        string tenantId,
        string approvalId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        HumanApprovalEntity? entity = await context.HumanApprovals.AsNoTracking()
            .SingleOrDefaultAsync(
                approval => approval.ApprovalId == approvalId
                    && approval.TenantId == tenantId,
                cancellationToken)
            .ConfigureAwait(false);
        return entity == null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<HumanApprovalRecord>> ListAsync(
        string tenantId,
        HumanApprovalStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        IQueryable<HumanApprovalEntity> query = context.HumanApprovals.AsNoTracking()
            .Where(approval => approval.TenantId == tenantId);
        if (status != null)
        {
            int statusValue = (int)status.Value;
            query = query.Where(approval => approval.Status == statusValue);
        }
        List<HumanApprovalEntity> entities = await query
            .OrderBy(approval => approval.ExpiresAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(ToRecord).ToList().AsReadOnly();
    }

    public async Task<HumanApprovalRecord?> TryTransitionAsync(
        string tenantId,
        string approvalId,
        HumanApprovalStatus expectedStatus,
        HumanApprovalStatus newStatus,
        string actorId,
        string? reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        HumanApprovalEntity? entity = await context.HumanApprovals.SingleOrDefaultAsync(
            approval => approval.ApprovalId == approvalId
                && approval.TenantId == tenantId
                && approval.Status == (int)expectedStatus,
            cancellationToken).ConfigureAwait(false);
        if (entity == null
            || expectedStatus is HumanApprovalStatus.Pending or HumanApprovalStatus.Preparing
                && newStatus != HumanApprovalStatus.Expired
                && entity.ExpiresAt <= decidedAt)
        {
            return null;
        }

        entity.Status = (int)newStatus;
        entity.DecidedBy = newStatus == HumanApprovalStatus.Pending ? null : actorId;
        entity.DecidedAt = newStatus == HumanApprovalStatus.Pending ? null : decidedAt;
        entity.DecisionReason = newStatus == HumanApprovalStatus.Pending ? null : reason;
        entity.Version++;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<HumanApprovalRecord>> ExpirePendingAsync(
        DateTimeOffset expiresAtOrBefore,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        IQueryable<HumanApprovalEntity> query = context.HumanApprovals.AsNoTracking()
            .Where(approval => approval.Status == (int)HumanApprovalStatus.Pending
                || approval.Status == (int)HumanApprovalStatus.Preparing)
            .Where(approval => approval.ExpiresAt <= expiresAtOrBefore);
        if (tenantId != null)
        {
            query = query.Where(approval => approval.TenantId == tenantId);
        }
        List<HumanApprovalEntity> candidates = await query
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HumanApprovalRecord> expired = [];
        foreach (HumanApprovalEntity candidate in candidates)
        {
            HumanApprovalRecord? transitioned = await TryTransitionAsync(
                candidate.TenantId,
                candidate.ApprovalId,
                (HumanApprovalStatus)candidate.Status,
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

    private static HumanApprovalEntity ToEntity(HumanApprovalRecord approval) => new()
    {
        ApprovalId = approval.ApprovalId,
        TenantId = approval.TenantId,
        ConversationId = approval.ConversationId,
        AgentId = approval.AgentId,
        TraceId = approval.TraceId,
        Action = approval.Action,
        TargetType = (int)approval.TargetType,
        TargetCapability = approval.TargetCapability,
        RedactedArgumentsJson = approval.RedactedArgumentsJson,
        RequestedBy = approval.RequestedBy,
        CreatedAt = approval.CreatedAt,
        ExpiresAt = approval.ExpiresAt,
        Status = (int)approval.Status,
        DecidedBy = approval.DecidedBy,
        DecidedAt = approval.DecidedAt,
        DecisionReason = approval.DecisionReason,
        MafRequestId = approval.MafRequestId,
        ToolCallId = approval.ToolCallId,
        ToolName = approval.ToolName,
        SessionStateJson = approval.SessionStateJson,
        RequesterContextJson = approval.RequesterContextJson,
        Version = approval.Version
    };

    private static HumanApprovalRecord ToRecord(HumanApprovalEntity entity) => new()
    {
        ApprovalId = entity.ApprovalId,
        TenantId = entity.TenantId,
        ConversationId = entity.ConversationId,
        AgentId = entity.AgentId,
        TraceId = entity.TraceId,
        Action = entity.Action,
        TargetType = (AgentResourceType)entity.TargetType,
        TargetCapability = entity.TargetCapability,
        RedactedArgumentsJson = entity.RedactedArgumentsJson,
        RequestedBy = entity.RequestedBy,
        CreatedAt = entity.CreatedAt,
        ExpiresAt = entity.ExpiresAt,
        Status = (HumanApprovalStatus)entity.Status,
        DecidedBy = entity.DecidedBy,
        DecidedAt = entity.DecidedAt,
        DecisionReason = entity.DecisionReason,
        MafRequestId = entity.MafRequestId,
        ToolCallId = entity.ToolCallId,
        ToolName = entity.ToolName,
        SessionStateJson = entity.SessionStateJson,
        RequesterContextJson = entity.RequesterContextJson,
        Version = entity.Version
    };
}
