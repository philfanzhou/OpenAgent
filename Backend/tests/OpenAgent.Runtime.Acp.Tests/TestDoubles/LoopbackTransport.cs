using System.Threading.Channels;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

/// <summary>
/// An in-memory transport pair: one side's writes become the other side's reads. Each side
/// also taps every frame it writes, so tests observe engine output without racing the peer
/// engine for the same channel.
/// </summary>
public sealed class LoopbackTransport : IAcpTransport
{
    private readonly Channel<byte[]> _inbound;
    private readonly Channel<byte[]> _tap = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private LoopbackTransport _peer = null!;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Exception? _fault;
    private bool _ended;

    private LoopbackTransport()
    {
        _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }

    public static (LoopbackTransport ClientSide, LoopbackTransport AgentSide) CreatePair()
    {
        LoopbackTransport clientSide = new();
        LoopbackTransport agentSide = new();
        clientSide._peer = agentSide;
        agentSide._peer = clientSide;
        return (clientSide, agentSide);
    }

    public Task Completion => _completion.Task;

    /// <summary>Frames this side's engine wrote, in order, for harness observation.</summary>
    public ChannelReader<byte[]> OutgoingFrames => _tap.Reader;

    /// <summary>Makes this side's ReadAsync return null (EOF), as when the peer process exits. Completion settles like a real transport.</summary>
    public void SimulateEof()
    {
        _inbound.Writer.TryComplete();
        _completion.TrySetResult();
    }

    /// <summary>Faults this side: subsequent writes fail and the engine observes a reader failure.</summary>
    public void Fault(Exception exception)
    {
        lock (_gate)
        {
            _fault ??= exception;
            _inbound.Writer.TryComplete();
        }

        _completion.TrySetException(exception);
    }

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            return _inbound.Reader.TryRead(out byte[]? frame) ? frame : null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        byte[] copy;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ended, this);
            if (_fault is not null) throw new IOException("Loopback transport faulted.", _fault);
            if (_inbound.Reader.Completion.IsCompleted) throw new IOException("Loopback transport saw EOF; writes are rejected.");
            copy = message.ToArray();
        }

        await _tap.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
        await _peer._inbound.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) _ended = true;
        _inbound.Writer.TryComplete();
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
