using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Security;

/// <summary>Fails closed on revocation, changed configuration, or an unavailable authority.</summary>
internal sealed class AuthorizationLease : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _execution;
    private readonly Task _monitor;
    private Exception? _failure;
    private readonly Func<CancellationToken, Task<bool>>? _revalidate;
    private readonly TimeSpan _timeout;

    internal AuthorizationLease(Func<CancellationToken, Task<bool>>? revalidate, CancellationToken caller,
        TimeSpan interval, TimeSpan timeout)
    {
        _revalidate = revalidate;
        _timeout = timeout;
        _execution = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _monitor = revalidate is null ? Task.CompletedTask : MonitorAsync(revalidate, interval, timeout);
    }

    internal CancellationToken Token => _execution.Token;

    internal async Task CheckAsync()
    {
        ThrowIfRevoked();
        _execution.Token.ThrowIfCancellationRequested();
        if (_revalidate is null) return;
        using CancellationTokenSource check = CancellationTokenSource.CreateLinkedTokenSource(_execution.Token);
        check.CancelAfter(_timeout);
        try
        {
            if (!await _revalidate(check.Token).WaitAsync(check.Token).ConfigureAwait(false))
                throw new InvalidOperationException("Runtime access was revoked or configuration changed.");
        }
        catch (Exception error) when (!_execution.IsCancellationRequested)
        {
            Volatile.Write(ref _failure, error);
            await CancelExecutionAsync().ConfigureAwait(false);
            ThrowIfRevoked();
        }
    }

    internal Exception ResolveFailure(Exception original) => Volatile.Read(ref _failure) is { } error
        ? new AgentException(AgentErrorCode.PermissionDenied, "Runtime authorization is no longer valid.", innerException: error)
        : original;

    internal void ThrowIfRevoked()
    {
        if (Volatile.Read(ref _failure) is { } error)
            throw new AgentException(AgentErrorCode.PermissionDenied, "Runtime authorization is no longer valid.", innerException: error);
    }

    private async Task MonitorAsync(Func<CancellationToken, Task<bool>> revalidate, TimeSpan interval, TimeSpan timeout)
    {
        try
        {
            while (!_stop.IsCancellationRequested && !_execution.IsCancellationRequested)
            {
                using CancellationTokenSource check = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _execution.Token);
                check.CancelAfter(timeout);
                if (!await revalidate(check.Token).WaitAsync(check.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("Runtime access was revoked or configuration changed.");
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || _execution.IsCancellationRequested) { }
        catch (Exception error)
        {
            Volatile.Write(ref _failure, error);
            await CancelExecutionAsync().ConfigureAwait(false);
        }
    }

    private async Task CancelExecutionAsync()
    {
        try { await _execution.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* Cancellation callbacks cannot replace the revocation cause. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _monitor.ConfigureAwait(false); }
        finally { _stop.Dispose(); _execution.Dispose(); }
    }
}
