namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

internal sealed class FragmentedReadStream(byte[] bytes, int maxReadBytes = 1) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(maxReadBytes, buffer.Length)], cancellationToken);
}
