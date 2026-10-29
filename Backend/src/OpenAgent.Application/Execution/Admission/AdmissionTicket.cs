using System.Diagnostics;

namespace OpenAgent.Application.Execution.Admission;

internal sealed class AdmissionTicket(TurnAdmission owner, string tenant) : IDisposable
{
    internal string Tenant { get; } = tenant;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal LinkedListNode<AdmissionTicket>? Node { get; set; }
    internal long EnqueuedAt { get; } = Stopwatch.GetTimestamp();
    internal long StartedAt { get; set; }
    internal bool Ended { get; set; }
    internal bool WasQueued { get; set; }
    private CancellationTokenRegistration _cancellation;
    private Timer? _timer;

    internal void Arm(CancellationToken ct, TimeSpan wait)
    {
        // Both callbacks only remove WAITING entries. Active ownership lasts through cleanup.
        _cancellation = ct.Register(() => owner.CancelWaiting(this, ct));
        _timer = new Timer(_ => owner.End(this, timeout: true), null, wait, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cancellation.Dispose();
        owner.End(this);
    }
}
