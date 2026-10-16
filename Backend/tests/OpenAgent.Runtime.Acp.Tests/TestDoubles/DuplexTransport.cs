using System.Threading.Channels;
using OpenAgent.Runtime.Acp.Transport;
namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

internal sealed class DuplexTransport(Channel<byte[]> input, Channel<byte[]> output) : IAcpTransport
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => _closed.Task;
    public async Task<byte[]?> ReadAsync(CancellationToken ct = default)
    { try { return await input.Reader.ReadAsync(ct); } catch (ChannelClosedException) { return null; } }
    public Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct = default) => output.Writer.WriteAsync(value.ToArray(), ct).AsTask();
    public ValueTask DisposeAsync() { input.Writer.TryComplete(); output.Writer.TryComplete(); _closed.TrySetResult(); return ValueTask.CompletedTask; }
    internal static (DuplexTransport Left, DuplexTransport Right) Pair()
    { Channel<byte[]> a = Channel.CreateUnbounded<byte[]>(), b = Channel.CreateUnbounded<byte[]>(); return (new(a, b), new(b, a)); }
}
