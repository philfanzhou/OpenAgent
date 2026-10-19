using System.Diagnostics;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Processes;

/// <summary>
/// Owns one SDK-started agent process: the framed transport handed to callers, the stderr
/// drain and the exit result. The lease is independent of sessions — disposing a session or
/// a client over the shared transport never ends the child (SPEC: ProcessLease outlives a
/// single session); only the child itself or the lease's staged shutdown ends the process.
/// </summary>
/// <remarks>
/// Staged shutdown (SPEC: ProcessShutdownGracePeriod, process by stages):
/// 1. end the framed transport — with the default options its disposal closes the stdin
///    pipe, the child's graceful EOF signal (LeaveOpen leaves that close to the lease);
/// 2. wait up to <see cref="AcpProcessOptions.ShutdownGracePeriod"/> for a natural exit;
/// 3. kill the whole tree;
/// 4. reap it and publish <see cref="Completion"/>.
/// Dispose is idempotent: repeat calls await the same published exit. <see cref="Completion"/>
/// also completes on a self-initiated child exit, so the exit code is observable without a
/// shutdown. The process id stays valid until <see cref="Completion"/> has completed; the
/// exit record's <see cref="AcpProcessExit.Failure"/> carries the cleanup failures observed
/// before the record published, and the transport itself keeps its own IO failure in its
/// Completion.
/// </remarks>
public sealed class AcpProcessLease : IAsyncDisposable
{
    private readonly Process _process;
    private readonly IAcpTransport _transport;
    private readonly AcpProcessOptions _options;
    private readonly TaskCompletionSource<AcpProcessExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _exited;
    private readonly Task _stderrPump;
    private readonly object _gate = new();
    private readonly List<Exception> _errors = [];
    private Task? _finish;
    private bool _killed;
    private int _disposeStarted;

    internal AcpProcessLease(Process process, IAcpTransport transport, AcpProcessOptions options, Task stderrPump)
    {
        _process = process;
        _transport = transport;
        _options = options;
        _stderrPump = stderrPump;
        _exited = process.WaitForExitAsync(CancellationToken.None);
        // A self-initiated exit must still drain stderr and publish its exit record.
        _ = _exited.ContinueWith(
            static (_, state) => ((AcpProcessLease)state!).FinishAsync(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>The framed transport over the child's stdio; hand it to a client or agent connection.</summary>
    public IAcpTransport Transport => _transport;

    /// <summary>The child's platform process id, for diagnostics only; valid until <see cref="Completion"/> completes.</summary>
    public int ProcessId => _process.Id;

    /// <summary>Completes after the child exited and the lease finished reaping; the record carries the code and kill flag.</summary>
    public Task<AcpProcessExit> Completion => _completion.Task;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _completion.Task.ConfigureAwait(false);
            return;
        }

        AddError(await DisposeTransportAsync().ConfigureAwait(false));
        if (_options.Transport?.LeaveOpen == true)
        {
            // The transport left the pipes open, so the graceful EOF signal is ours to send.
            try
            {
                _process.StandardInput.Dispose();
            }
            catch (Exception exception)
            {
                AddError(exception);
            }
        }

        // Stage 2 — a bounded window for the child to exit on the EOF it just received.
        if (!_exited.IsCompleted && !await TryWaitForExitAsync(_options.ShutdownGracePeriod).ConfigureAwait(false))
        {
            bool killed = false;
            try
            {
                _process.Kill(entireProcessTree: true);
                killed = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // The child exited inside the race window; the natural exit stays the record.
            }
            catch (Exception exception)
            {
                AddError(exception);
            }

            lock (_gate)
            {
                _killed = killed;
            }

            // Stages 3/4 — final reclaim. A tree kill converges; the bounded wait keeps
            // teardown observable and the unbounded fallback guards a stalled reap.
            if (killed && !await TryWaitForExitAsync(_options.ShutdownGracePeriod * 2).ConfigureAwait(false))
            {
                try
                {
                    await Task.Run(() => _process.WaitForExit()).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    AddError(exception);
                }
            }
        }

        await FinishAsync().ConfigureAwait(false);
    }

    /// <summary>Runs once; drains stderr, releases the process and publishes the exit record.</summary>
    private Task FinishAsync() => _finish ??= FinishCoreAsync();

    private async Task FinishCoreAsync()
    {
        await _exited.ConfigureAwait(false);
        // The stderr pipe closes with the process, so the drain settles immediately in
        // practice; the bound only guards a stray descendant still holding the write end.
        await Task.WhenAny(_stderrPump, Task.Delay(_options.ShutdownGracePeriod)).ConfigureAwait(false);
        int? exitCode = null;
        try
        {
            if (_process.HasExited) exitCode = _process.ExitCode;
        }
        catch (Exception exception)
        {
            AddError(exception);
        }

        try
        {
            _process.Dispose();
        }
        catch (Exception exception)
        {
            AddError(exception);
        }

        Exception? failure;
        lock (_gate)
        {
            failure = Combine(_errors);
        }

        _completion.TrySetResult(new AcpProcessExit { ExitCode = exitCode, Killed = _killed, Failure = failure });
    }

    private async Task<Exception?> DisposeTransportAsync()
    {
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task<bool> TryWaitForExitAsync(TimeSpan window)
    {
        if (_exited.IsCompleted) return true;
        using CancellationTokenSource timeout = new(window);
        try
        {
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The finish path already reaped and released the process; the exit task is the
            // surviving observation of whether the child is gone.
            return _exited.IsCompleted;
        }
    }

    private void AddError(Exception? exception)
    {
        if (exception is null) return;
        lock (_gate)
        {
            _errors.Add(exception);
        }
    }

    private static Exception? Combine(List<Exception> errors) => errors.Count switch
    {
        0 => null,
        1 => errors[0],
        _ => new AggregateException(errors)
    };
}
