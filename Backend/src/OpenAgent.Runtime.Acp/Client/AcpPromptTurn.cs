using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.Client;

/// <summary>
/// One prompt turn on a session. <see cref="Completion"/> carries the final response and
/// completes only after every update that preceded it on the wire is enqueued into the
/// session stream — completion never promises the consumer has processed them.
/// </summary>
public sealed class AcpPromptTurn : IAsyncDisposable
{
    /// <summary>Bounded cancel used when an unfinished turn is disposed; cancel grace applies (SPEC table: 5s).</summary>
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(5);

    private readonly AcpSession _session;
    private readonly TaskCompletionSource<PromptResponse> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal AcpPromptTurn(AcpSession session, IReadOnlyList<ContentBlock> prompt)
    {
        _session = session;
        Prompt = prompt;
    }

    /// <summary>The prompt content blocks sent for this turn.</summary>
    public IReadOnlyList<ContentBlock> Prompt { get; }

    /// <summary>The final prompt response, after preceding updates are enqueued. Fails when the connection closes first.</summary>
    public Task<PromptResponse> Completion => _completed.Task;

    internal void Start(CancellationToken cancellationToken)
    {
        // Send-side capability validation already happened in StartPromptAsync; this runs the request.
        _ = CoreAsync(cancellationToken);
    }

    /// <summary>Sends session/cancel; only guarantees the notification was written (SPEC).</summary>
    public Task CancelAsync(CancellationToken cancellationToken = default) =>
        _session.CancelAsync(cancellationToken);

    /// <summary>
    /// Sends session/cancel and awaits the original prompt response. A natural completion wins
    /// the race and is returned as-is; cancel-tail updates stay ordered ahead of completion.
    /// </summary>
    public async Task<PromptResponse> CancelAndWaitAsync(CancellationToken cancellationToken = default)
    {
        await CancelAsync(cancellationToken).ConfigureAwait(false);
        return await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disposing an unfinished turn cancels it with a bounded wait; a finished turn is untouched.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Completion.IsCompleted) return;
        try
        {
            await CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AcpConnectionClosedException or ObjectDisposedException)
        {
            // The link is gone; the turn already failed through Completion.
            return;
        }

        try
        {
            await Completion.WaitAsync(DisposeGrace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timeout or a failed link: the outcome stays on Completion for whoever awaits it.
        }
    }

    private async Task CoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            PromptRequest request = new(_session.SessionId, [.. Prompt]);
            Task<PromptResponse> response = _session.Connection.PromptAsync(request, cancellationToken);
            PromptResponse final = await response.ConfigureAwait(false);
            // Updates that preceded the response are in session queues before Completion reports (SPEC).
            await _session.Connection.AwaitUpdateDrainAsync(cancellationToken).ConfigureAwait(false);
            _completed.TrySetResult(final);
        }
        catch (Exception exception)
        {
            _completed.TrySetException(exception);
        }
        finally
        {
            _session.OnTurnFinished(this);
        }
    }
}
