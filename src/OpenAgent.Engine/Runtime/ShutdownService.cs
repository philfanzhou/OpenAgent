using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Runtime;

internal class ShutdownService : BackgroundService
{
    private readonly ILogger<ShutdownService> _logger;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, InFlightRequest> _inFlightRequests = new();
    private readonly SemaphoreSlim _shutdownSemaphore = new(0, 1);
    private volatile bool _isShuttingDown = false;

    public ShutdownService(ILogger<ShutdownService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(configuration.GetValue("Shutdown:TimeoutSeconds", 30));
    }

    internal string RegisterRequest(string requestType, string? traceId = null)
    {
        if (_isShuttingDown)
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                "Engine is shutting down, no new requests accepted");
        }

        var requestId = Guid.NewGuid().ToString("N")[..8];
        var request = new InFlightRequest
        {
            Id = requestId,
            RequestType = requestType,
            TraceId = traceId ?? requestId,
            StartTime = DateTime.UtcNow
        };

        _inFlightRequests.TryAdd(requestId, request);
        return requestId;
    }

    internal void CompleteRequest(string requestId)
    {
        _inFlightRequests.TryRemove(requestId, out _);
    }

    internal async Task ShutdownAsync(TimeSpan timeout)
    {
        EngineLog.ShutdownInitiated(_logger, timeout.TotalSeconds);
        _isShuttingDown = true;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (_inFlightRequests.Count > 0 && stopwatch.Elapsed < timeout)
        {
            var remaining = _inFlightRequests.Count;
            EngineLog.ShutdownWaitingForRequests(_logger, remaining);

            foreach (var request in _inFlightRequests.Values)
            {
                var duration = DateTime.UtcNow - request.StartTime;
                EngineLog.ShutdownPendingRequest(_logger, request.Id, request.RequestType, duration.TotalMilliseconds);
            }

            await Task.Delay(Math.Min(1000, (int)(timeout.TotalMilliseconds - stopwatch.ElapsedMilliseconds)),
                CancellationToken.None);
        }

        stopwatch.Stop();

        if (_inFlightRequests.Count > 0)
        {
            EngineLog.ShutdownTimeoutReached(_logger, _inFlightRequests.Count);

            foreach (var request in _inFlightRequests.Values)
            {
                var duration = DateTime.UtcNow - request.StartTime;
                EngineLog.ShutdownTimeoutRunningRequest(_logger, request.Id, request.RequestType, duration.TotalMilliseconds);
            }
        }
        else
        {
            EngineLog.ShutdownCompleted(_logger, stopwatch.ElapsedMilliseconds);
        }

        _shutdownSemaphore.Release();
    }

    internal int InFlightRequestCount => _inFlightRequests.Count;

    internal bool IsShuttingDown => _isShuttingDown;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _shutdownSemaphore.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await ShutdownAsync(_timeout);
        await base.StopAsync(cancellationToken);
    }

    private class InFlightRequest
    {
        public string Id { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public string TraceId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
    }
}
