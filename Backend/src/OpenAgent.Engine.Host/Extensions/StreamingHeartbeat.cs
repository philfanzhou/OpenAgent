using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Host.Extensions;

internal sealed class StreamingHeartbeat : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _writeHeartbeatAsync;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly string _endpoint;
    private readonly string _traceId;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _runTask;

    private StreamingHeartbeat(
        Func<CancellationToken, Task> writeHeartbeatAsync,
        TimeSpan interval,
        ILogger logger,
        string endpoint,
        string traceId,
        CancellationToken cancellationToken)
    {
        _writeHeartbeatAsync = writeHeartbeatAsync;
        _interval = interval;
        _logger = logger;
        _endpoint = endpoint;
        _traceId = traceId;
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync();
    }

    public static StreamingHeartbeat Start(
        Func<CancellationToken, Task> writeHeartbeatAsync,
        TimeSpan interval,
        ILogger logger,
        string endpoint,
        string traceId,
        CancellationToken cancellationToken)
    {
        return new StreamingHeartbeat(writeHeartbeatAsync, interval, logger, endpoint, traceId, cancellationToken);
    }

    public async Task WriteAsync(Func<CancellationToken, Task> writeAsync, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopTokenSource.CancelAsync().ConfigureAwait(false);

        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stopTokenSource.Dispose();
        _writeLock.Dispose();
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(_stopTokenSource.Token).ConfigureAwait(false))
        {
            try
            {
                await _writeLock.WaitAsync(_stopTokenSource.Token).ConfigureAwait(false);
                try
                {
                    await _writeHeartbeatAsync(_stopTokenSource.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException) when (_stopTokenSource.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                EngineLog.StreamingHeartbeatFailed(_logger, ex, _endpoint, _traceId);
                return;
            }
        }
    }
}
