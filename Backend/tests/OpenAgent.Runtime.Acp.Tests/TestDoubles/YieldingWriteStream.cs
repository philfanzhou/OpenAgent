namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

internal sealed class YieldingWriteStream : MemoryStream
{
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        await base.WriteAsync(buffer, cancellationToken);
    }
}
