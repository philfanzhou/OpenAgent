namespace OpenAgent.Router.Middleware;

internal sealed class LimitedCaptureStream(Stream destination, int limit) : Stream
{
    private readonly MemoryStream _capture = new();

    internal bool IsComplete { get; private set; } = true;

    internal byte[] GetCapturedBody() => _capture.ToArray();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => destination.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => destination.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        destination.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        destination.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        Capture(buffer.AsSpan(offset, count));
        await destination.WriteAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Capture(buffer);
        destination.Write(buffer);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private void Capture(ReadOnlySpan<byte> buffer)
    {
        if (!IsComplete)
        {
            return;
        }

        if (_capture.Length + buffer.Length > limit)
        {
            IsComplete = false;
            _capture.SetLength(0);
            return;
        }

        _capture.Write(buffer);
    }
}
