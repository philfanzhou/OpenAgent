using System.Runtime.ExceptionServices;

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
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task<Exception?>? _shutdown;
    private Exception? _failure;
    private int _offset;
    private int _count;
    private int _activeOperations;
    private bool _reading;
    private bool _closing;
    private bool _disposed;

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
        EnterOperation(isRead: true);
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

                        _ = StartClosing(cancelOperations: false);
                        return null;
                    }
                }

                int newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
                int end = newline < 0 ? _count : newline;
                int length = end - _offset;
                long frameBytes = _line.Length + length;
                bool trailingCr = length > 0
                    ? _buffer[end - 1] == (byte)'\r'
                    : _line.Length > 0 && _line.GetBuffer()[(int)_line.Length - 1] == (byte)'\r';
                // Only one trailing CR may be a delimiter candidate, including across reads.
                if (frameBytes > _options.MaxMessageBytes &&
                    !(frameBytes == (long)_options.MaxMessageBytes + 1 && trailingCr))
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
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = StartClosing(exception);
            throw;
        }
        finally
        {
            ExitOperation(isRead: true);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        EnterOperation(isRead: false);
        try
        {
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
                lock (_gate)
                {
                    ThrowIfUnavailable();
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Once a frame starts, caller cancellation cannot truncate it.
                    await _output.WriteAsync(message, _lifetime.Token).ConfigureAwait(false);
                    await _output.WriteAsync(Newline, _lifetime.Token).ConfigureAwait(false);
                    await _output.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _ = StartClosing(exception);
                    throw;
                }
            }
            finally
            {
                _writer.Release();
            }
        }
        finally
        {
            ExitOperation(isRead: false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Exception? failure = await StartClosing(disposing: true).ConfigureAwait(false);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private Task<Exception?> StartClosing(Exception? failure = null, bool disposing = false, bool cancelOperations = true)
    {
        lock (_gate)
        {
            _failure ??= failure;
            _disposed |= disposing;
            _closing = true;
            if (_activeOperations == 0)
            {
                _drained.TrySetResult();
            }

            if (cancelOperations)
            {
                _stopRequested.TrySetResult();
            }

            // Publish one shutdown task before executing any custom Stream callbacks.
            // Its result carries cleanup failures; it never leaves an unobserved faulted task.
            return _shutdown ??= Task.Run(ShutdownAsync);
        }
    }

    private async Task<Exception?> ShutdownAsync()
    {
        // EOF drains an already-started frame; Dispose or a fault can interrupt that drain.
        await Task.WhenAny(_drained.Task, _stopRequested.Task).ConfigureAwait(false);
        List<Exception> errors = [];
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (!_options.LeaveOpen)
        {
            try
            {
                await _input.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            if (!ReferenceEquals(_input, _output))
            {
                try
                {
                    await _output.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
        }

        await _drained.Task.ConfigureAwait(false);
        _line.Dispose();
        _writer.Dispose();
        _lifetime.Dispose();
        Exception? cleanupFailure = errors.Count switch
        {
            0 => null,
            1 => errors[0],
            _ => new AggregateException(errors)
        };
        Exception? terminalFailure;
        lock (_gate)
        {
            terminalFailure = _failure is null ? cleanupFailure
                : cleanupFailure is null ? _failure
                : new AggregateException(_failure, cleanupFailure);
        }

        if (terminalFailure is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(terminalFailure);
        }

        return cleanupFailure;
    }

    private void EnterOperation(bool isRead)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (isRead && _reading)
            {
                throw new InvalidOperationException("Only one transport reader is allowed.");
            }

            _reading |= isRead;
            _activeOperations++;
        }
    }

    private void ExitOperation(bool isRead)
    {
        lock (_gate)
        {
            if (isRead)
            {
                _reading = false;
            }

            if (--_activeOperations == 0 && _closing)
            {
                _drained.TrySetResult();
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null)
        {
            throw new IOException("ACP transport has faulted.", _failure);
        }

        if (_closing)
        {
            throw new IOException("ACP transport is closing.");
        }
    }
}
