using System.Threading.Channels;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

/// <summary>
/// A transport whose WriteAsync parks until released and then faults, so a write can be held
/// in flight deterministically while the connection closes — no scheduling race involved.
/// </summary>
public sealed class BlockingWriteTransport : IAcpTransport
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public Task Completion => _completion.Task;

    /// <summary>Completes once the engine's writer loop is parked inside WriteAsync.</summary>
    public Task WriteEntered => _entered.Task;

    /// <summary>Faults the transport the way a real broken pipe does: reader EOF plus terminal fault.</summary>
    public void Fault(Exception exception)
    {
        _inbound.Writer.TryComplete();
        _completion.TrySetException(exception);
    }

    public void ReleaseWrite() => _release.TrySetResult();

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        return await _inbound.Reader.WaitToReadAsync(CancellationToken.None) ? null : null;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        await _release.Task;
        throw new IOException("The transport failed mid-write.");
    }

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
