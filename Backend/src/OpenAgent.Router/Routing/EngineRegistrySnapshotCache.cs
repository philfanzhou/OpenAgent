using System.Text.Json;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Routing;
using StackExchange.Redis;

namespace OpenAgent.Router;

public sealed class EngineRegistrySnapshotCache : BackgroundService
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<EngineRegistrySnapshotCache> _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeProvider _timeProvider;
    private readonly RegistryPoller _poller;
    private volatile IReadOnlyList<EngineRegistryEntry> _snapshot = Array.Empty<EngineRegistryEntry>();

    public IReadOnlyList<EngineRegistryEntry> Snapshot => _snapshot;

    public EngineRegistrySnapshotCache(IConnectionMultiplexer? redis, ILogger<EngineRegistrySnapshotCache> logger)
        : this(redis, logger, TimeSpan.FromSeconds(5), TimeProvider.System)
    {
    }

    public EngineRegistrySnapshotCache(
        IConnectionMultiplexer? redis,
        ILogger<EngineRegistrySnapshotCache> logger,
        TimeSpan refreshInterval,
        TimeProvider timeProvider)
    {
        _redis = redis;
        _logger = logger;
        _refreshInterval = refreshInterval;
        _timeProvider = timeProvider;
        _poller = new RegistryPoller(refreshInterval, timeProvider, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _poller.RunAsync(RefreshAsync, stoppingToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _snapshot = await LoadHealthyEnginesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RouterLog.RefreshSnapshotPreservingPrevious(_logger, ex);
        }
    }

    private async Task<IReadOnlyList<EngineRegistryEntry>> LoadHealthyEnginesAsync(CancellationToken cancellationToken)
    {
        if (_redis == null)
        {
            return Array.Empty<EngineRegistryEntry>();
        }

        var db = _redis.GetDatabase();
        var endpoint = _redis.GetEndPoints().FirstOrDefault();
        if (endpoint == null)
        {
            return Array.Empty<EngineRegistryEntry>();
        }

        var server = _redis.GetServer(endpoint);
        var engineKeys = server.Keys(pattern: "engine:registry:*").ToArray();

        if (engineKeys.Length == 0)
        {
            return Array.Empty<EngineRegistryEntry>();
        }

        var healthyEngines = new List<EngineRegistryEntry>();

        foreach (var key in engineKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = db.StringGet(key);
            if (json.IsNullOrEmpty) continue;

            try
            {
                var entry = JsonSerializer.Deserialize<EngineRegistryEntry>(json.ToString(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (entry == null) continue;

                var heartbeatAge = DateTime.UtcNow - entry.LastHeartbeat;
                if (heartbeatAge > TimeSpan.FromSeconds(60))
                {
                    RouterLog.EngineHeartbeatStale(_logger, entry.EngineId, heartbeatAge.TotalSeconds);
                    continue;
                }

                healthyEngines.Add(entry);
            }
            catch (JsonException ex)
            {
                RouterLog.EngineEntryDeserializationFailed(_logger, ex, key);
            }
        }

        return healthyEngines;
    }
}
