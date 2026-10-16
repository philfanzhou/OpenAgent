namespace OpenAgent.Runtime.Acp.Transport;

/// <summary>Bounded NDJSON framing with one reader and serialized writers. JSON validation belongs to RPC.</summary>
public sealed class AcpStreamTransport : IAcpTransport
{
    private static readonly byte[] Newline = [(byte)'\n'];
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly AcpTransportOptions _options;
    private readonly byte[] _buffer = new byte[4096];
    private readonly MemoryStream _line = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failure;
    private int _offset;
    private int _count;
    private int _reading;
    private int _disposed;

    public AcpStreamTransport(Stream input, Stream output, AcpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _options = options ?? new AcpTransportOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxMessageBytes);
        if (!input.CanRead || !output.CanWrite)
        {
            throw new ArgumentException("Input must be readable and output must be writable.");
        }

        _input = input;
        _output = output;
    }

    public Task Completion => _completion.Task;

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException("Only one transport reader is allowed.");
        }

        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (_offset == _count)
                {
                    _count = await _input.ReadAsync(_buffer, linked.Token).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        if (_line.Length != 0)
                        {
                            throw new EndOfStreamException("ACP message ended before its newline delimiter.");
                        }

                        _completion.TrySetResult();
                        return null;
                    }
                }

                int newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
                int end = newline < 0 ? _count : newline;
                int length = end - _offset;
                if (_line.Length + length > _options.MaxMessageBytes)
                {
                    throw new InvalidDataException("ACP message exceeds MaxMessageBytes.");
                }

                _line.Write(_buffer, _offset, length);
                _offset = newline < 0 ? end : end + 1;
                if (newline >= 0)
                {
                    byte[] message = _line.ToArray();
                    _line.SetLength(0);
                    if (message.Length > 0 && message[^1] == (byte)'\r')
                    {
                        Array.Resize(ref message, message.Length - 1);
                    }

                    return message;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Fail(exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (message.IsEmpty || message.Span.IndexOfAny((byte)'\n', (byte)'\r') >= 0)
        {
            throw new ArgumentException("Message must be a nonempty single-line body.", nameof(message));
        }

        if (message.Length > _options.MaxMessageBytes)
        {
            throw new InvalidDataException("ACP message exceeds MaxMessageBytes.");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        await _writer.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            // Once a frame starts, only closing the transport may interrupt it.
            await _output.WriteAsync(message, _lifetime.Token).ConfigureAwait(false);
            await _output.WriteAsync(Newline, _lifetime.Token).ConfigureAwait(false);
            await _output.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Fail(exception);
            throw;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (!_options.LeaveOpen)
            {
                try
                {
                    await _input.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (!ReferenceEquals(_input, _output))
                    {
                        await _output.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            _completion.TrySetResult();
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
            throw;
        }
        // Synchronization objects can still be in use by operations unwinding cancellation.
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_failure is not null)
        {
            throw new IOException("ACP transport has faulted.", _failure);
        }
    }

    private void Fail(Exception exception)
    {
        Interlocked.CompareExchange(ref _failure, exception, null);
        _completion.TrySetException(exception);
        _lifetime.Cancel();
    }
}
