using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Application.Execution.Admission;

/// <summary>FIFO among eligible tenants; a saturated tenant cannot block other tenants.</summary>
internal sealed class TurnAdmission(IOptions<AdmissionOptions> options)
{
    private readonly AdmissionOptions _limits = options.Value;
    private readonly object _gate = new();
    private readonly LinkedList<AdmissionTicket> _waiting = new();
    private readonly Dictionary<string, int> _activeTenants = new(StringComparer.Ordinal);
    private int _active;

    internal AdmissionTicket Enter(string tenant, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AdmissionTicket ticket = new(this, tenant);
        lock (_gate)
        {
            if (_active < _limits.MaxConcurrentTurns && ActiveFor(tenant) < _limits.MaxConcurrentPerTenant)
            {
                Start(ticket);
            }
            else
            {
                if (_waiting.Count >= _limits.MaxQueuedTurns
                    || _waiting.Count(item => item.Tenant == tenant) >= _limits.MaxQueuedPerTenant)
                {
                    AdmissionMetrics.Rejected.Add(1);
                    throw new RuntimeAdmissionException(AgentErrorCode.RuntimeQueueFull,
                        "Runtime capacity is busy. Retry after the indicated delay.", _limits.RetryAfterSeconds);
                }
                ticket.Node = _waiting.AddLast(ticket);
                ticket.WasQueued = true;
                AdmissionMetrics.Waiting.Add(1);
            }
        }
        // Cancellation may run synchronously. Register outside the scheduler lock.
        ticket.Arm(ct, TimeSpan.FromSeconds(_limits.QueueTimeoutSeconds));
        return ticket;
    }

    private int ActiveFor(string tenant) => _activeTenants.GetValueOrDefault(tenant);

    private void Start(AdmissionTicket ticket)
    {
        ticket.StartedAt = Stopwatch.GetTimestamp();
        _active++;
        _activeTenants[ticket.Tenant] = ActiveFor(ticket.Tenant) + 1;
        AdmissionMetrics.Active.Add(1);
        AdmissionMetrics.QueueTime.Record(Stopwatch.GetElapsedTime(ticket.EnqueuedAt).TotalSeconds);
        ticket.Ready.TrySetResult();
    }

    internal void CancelWaiting(AdmissionTicket ticket, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ticket.StartedAt == 0) End(ticket, ct: ct);
        }
    }

    internal void End(AdmissionTicket ticket, bool timeout = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (ticket.Ended)
            {
                return;
            }
            // Queue deadlines only govern waiting, never an already admitted execution.
            if (timeout && ticket.StartedAt != 0)
            {
                return;
            }
            ticket.Ended = true;
            if (ticket.Node is { } node)
            {
                _waiting.Remove(node);
                ticket.Node = null;
                AdmissionMetrics.Waiting.Add(-1);
                AdmissionMetrics.QueueTime.Record(Stopwatch.GetElapsedTime(ticket.EnqueuedAt).TotalSeconds);
                if (timeout)
                {
                    ticket.Ready.TrySetException(new RuntimeAdmissionException(AgentErrorCode.RuntimeQueueTimeout,
                        "Runtime queue wait timed out. Retry after the indicated delay.", _limits.RetryAfterSeconds));
                }
                else
                {
                    ticket.Ready.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true));
                }
            }
            else if (ticket.StartedAt != 0)
            {
                _active--;
                int count = ActiveFor(ticket.Tenant) - 1;
                if (count == 0) _activeTenants.Remove(ticket.Tenant);
                else _activeTenants[ticket.Tenant] = count;
                AdmissionMetrics.Active.Add(-1);
                AdmissionMetrics.ExecutionTime.Record(Stopwatch.GetElapsedTime(ticket.StartedAt).TotalSeconds);
            }
            LinkedListNode<AdmissionTicket>? next = _waiting.First;
            while (_active < _limits.MaxConcurrentTurns && next is not null)
            {
                LinkedListNode<AdmissionTicket> current = next;
                next = next.Next;
                if (ActiveFor(current.Value.Tenant) >= _limits.MaxConcurrentPerTenant)
                {
                    continue;
                }
                _waiting.Remove(current);
                current.Value.Node = null;
                AdmissionMetrics.Waiting.Add(-1);
                Start(current.Value);
            }
        }
    }
}
