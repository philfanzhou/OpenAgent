using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Approvals;

internal sealed class InMemoryHumanApprovalStore
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);

    internal bool Add(PendingApproval approval) => _pending.TryAdd(approval.Request.ApprovalId, approval);

    internal bool TryTake(
        string tenantId,
        string approvalId,
        DateTimeOffset now,
        out PendingApproval? approval)
    {
        approval = null;
        if (!_pending.TryGetValue(approvalId, out PendingApproval? candidate))
        {
            return false;
        }

        if (!string.Equals(candidate.Request.TenantId, tenantId, StringComparison.Ordinal))
        {
            return false;
        }

        if (candidate.Request.ExpiresAt <= now)
        {
            ((ICollection<KeyValuePair<string, PendingApproval>>)_pending)
                .Remove(new KeyValuePair<string, PendingApproval>(approvalId, candidate));
            return false;
        }

        bool removed = ((ICollection<KeyValuePair<string, PendingApproval>>)_pending).Remove(
            new KeyValuePair<string, PendingApproval>(approvalId, candidate));
        approval = removed ? candidate : null;
        return removed;
    }
}

internal sealed record PendingApproval(
    HumanApprovalRequest Request,
    ToolApprovalRequestContent Content,
    string SessionStateJson,
    AgentRequest AgentRequest,
    AgentUserContext Requester);
