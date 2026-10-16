using System.Text.Json;

namespace OpenAgent.Acp.Demo;

/// <summary>Bounded UTF-8 NDJSON over a pair of owned streams.</summary>
internal sealed class JsonLineTransport(Stream input, Stream output) : IAsyncDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly byte[] _buffer = new byte[4096];
    private readonly SemaphoreSlim _writer = new(1, 1);
    private int _offset;
    private int _count;

    internal async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        using MemoryStream line = new();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await input.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0)
                {
                    if (line.Length != 0)
                    {
                        throw new EndOfStreamException("ACP message ended before its newline delimiter.");
                    }

                    return null;
                }
            }

            int newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            int end = newline < 0 ? _count : newline;
            int length = end - _offset;
            if (line.Length + length > MaxMessageBytes)
            {
                throw new InvalidDataException("ACP message exceeds the demo's 1 MiB limit.");
            }

            line.Write(_buffer, _offset, length);
            _offset = newline < 0 ? end : end + 1;
            if (newline >= 0)
            {
                return line.ToArray();
            }
        }
    }

    internal async Task WriteAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("ACP message exceeds the demo's 1 MiB limit.");
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Cancellation may prevent a frame from starting, but must not cut JSON in half.
            await output.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await input.DisposeAsync().ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);
        _writer.Dispose();
    }
}
