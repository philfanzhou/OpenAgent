using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.Client;

/// <summary>
/// One agent session on a client connection: the ordered single-consumer update stream, one
/// prompt turn at a time, and cancellation. Disposal releases the local association; it never
/// kills shared resources (SPEC).
/// </summary>
public sealed class AcpSession : IAsyncDisposable
{
    private readonly AcpClientConnection _connection;
    private readonly Channel<SessionNotification> _updates;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _turnGate = new();
    private AcpPromptTurn? _activeTurn;
    private int _consumerActive;
    private int _disposed;

    internal AcpSession(AcpClientConnection connection, string sessionId, int maxQueuedUpdates)
    {
        _connection = connection;
        SessionId = sessionId;
        _updates = Channel.CreateBounded<SessionNotification>(new BoundedChannelOptions(maxQueuedUpdates)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public string SessionId { get; }

    public AcpClientConnection Connection => _connection;

    /// <summary>
    /// Streams session updates in wire order for exactly one consumer at a time; a second
    /// concurrent consumer fails. Once an enumeration has ended, a new consumer may start and
    /// continues from the next queued update. Start consuming before prompting: updates that
    /// precede the final response are already queued when the turn completes.
    /// </summary>
    public IAsyncEnumerable<SessionNotification> ReadUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _consumerActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("ReadUpdatesAsync supports exactly one consumer per session.");
        }

        return ReadCoreAsync(cancellationToken);
    }

    /// <summary>
    /// Starts one prompt turn. The SDK allows a single active turn per session: a second
    /// concurrent start fails immediately with <see cref="SessionBusyException"/>; different
    /// sessions may prompt concurrently. Validation failures surface through the task.
    /// </summary>
    public async Task<AcpPromptTurn> StartPromptAsync(IReadOnlyList<ContentBlock> prompt, CancellationToken cancellationToken = default)
    {
        await Task.Yield(); // Let validation failures flow through the returned task, like every other start failure.
        return StartPromptCore(prompt, cancellationToken);
    }

    private AcpPromptTurn StartPromptCore(IReadOnlyList<ContentBlock> prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _connection.ValidatePromptContent(prompt);
        AcpPromptTurn turn = new(this, prompt);
        lock (_turnGate)
        {
            if (_activeTurn is not null) throw new SessionBusyException(SessionId);
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(AcpSession));
            _activeTurn = turn;
        }

        try
        {
            turn.Start(cancellationToken);
        }
        catch
        {
            lock (_turnGate)
            {
                if (ReferenceEquals(_activeTurn, turn)) _activeTurn = null;
            }

            throw;
        }

        return turn;
    }

    /// <summary>Sends session/cancel. The notification write is the only guarantee; the final prompt response confirms the cancel.</summary>
    public Task CancelAsync(CancellationToken cancellationToken = default) =>
        _connection.SendCancelAsync(SessionId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AcpPromptTurn? turn;
        lock (_turnGate)
        {
            turn = _activeTurn;
        }

        // An unfinished turn gets a bounded cancel; the dispose bound never reports a fake outcome.
        if (turn is not null) await turn.DisposeAsync().ConfigureAwait(false);
        _lifetime.Cancel();
        _lifetime.Dispose();
        _updates.Writer.TryComplete();
    }

    internal void Enqueue(SessionNotification update)
    {
        if (_updates.Writer.TryWrite(update)) return;
        int limit = _connection.MaxQueuedUpdates;
        // The reader must never stall behind a slow consumer; overflow faults the connection (SPEC).
        _ = _connection.DisposeAsync();
        throw new AcpQuotaExceededException(nameof(AcpClientOptions.MaxQueuedUpdates), limit, limit + 1L);
    }

    internal void OnTurnFinished(AcpPromptTurn turn)
    {
        lock (_turnGate)
        {
            if (ReferenceEquals(_activeTurn, turn)) _activeTurn = null;
        }
    }

    internal void OnConnectionClosed()
    {
        // A disposed session already completed its own stream; cancelling its token again would
        // throw through the connection's fan-out and strand every remaining session (review F3).
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _updates.Writer.TryComplete();
    }

    private async IAsyncEnumerable<SessionNotification> ReadCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            while (await _updates.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
            {
                while (_updates.Reader.TryRead(out SessionNotification? update)) yield return update;
            }
        }
        finally
        {
            // The consumer slot frees when this enumeration ends — completed, broken, or
            // cancelled — so a later consumer can start; consumers still never overlap (review F5).
            Volatile.Write(ref _consumerActive, 0);
        }
    }
}
