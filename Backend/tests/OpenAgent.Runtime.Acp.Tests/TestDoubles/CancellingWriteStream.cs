namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

internal sealed class CancellingWriteStream(CancellationTokenSource cancellation) : MemoryStream
{
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await base.WriteAsync(buffer, cancellationToken);
        cancellation.Cancel();
    }
}
