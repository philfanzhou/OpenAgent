using System.Text.Json;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using StackExchange.Redis;

namespace OpenAgent.Router;

public sealed class EngineRegistrySnapshotCache : BackgroundService
{
    internal const string RegistryIndexKey = "engine:registry:index";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<EngineRegistrySnapshotCache> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RegistryPoller _poller;
    private readonly ServiceDiscoverySettings _settings;
    private volatile IReadOnlyList<EngineRegistryEntry> _snapshot = Array.Empty<EngineRegistryEntry>();
    private DateTimeOffset? _lastSuccessfulRefresh;
    private volatile bool _isRedisAvailable;

    public IReadOnlyList<EngineRegistryEntry> Snapshot => _snapshot;
    public bool IsRedisAvailable => _isRedisAvailable;
    public DateTimeOffset? LastSuccessfulRefresh => _lastSuccessfulRefresh;

    public EngineRegistrySnapshotCache(
        IConnectionMultiplexer? redis,
        ILogger<EngineRegistrySnapshotCache> logger,
        IConfiguration configuration)
        : this(
            redis,
            logger,
            ServiceDiscoverySettings.FromConfiguration(configuration),
            TimeProvider.System)
    {
    }

    internal EngineRegistrySnapshotCache(
        IConnectionMultiplexer? redis,
        ILogger<EngineRegistrySnapshotCache> logger,
        ServiceDiscoverySettings settings,
        TimeProvider timeProvider)
    {
        _redis = redis;
        _logger = logger;
        _settings = settings;
        _timeProvider = timeProvider;
        _poller = new RegistryPoller(settings.RefreshInterval, timeProvider, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _poller.RunAsync(RefreshAsync, stoppingToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<EngineRegistryEntry> engines = await LoadHealthyEnginesAsync(
                cancellationToken).ConfigureAwait(false);
            _snapshot = engines;
            _isRedisAvailable = true;
            _lastSuccessfulRefresh = _timeProvider.GetUtcNow();
            RouterMeter.RecordDiscoveryRefresh("success", engines.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _isRedisAvailable = false;
            RouterMeter.RecordDiscoveryRefresh("redis_error", _snapshot.Count);
            RouterLog.RefreshSnapshotUnavailable(_logger, ex, _settings.RedisFailureMode.ToString());

            if (!CanUseLastKnownSnapshot())
            {
                _snapshot = [];
            }
        }
    }

    private async Task<IReadOnlyList<EngineRegistryEntry>> LoadHealthyEnginesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_redis == null)
        {
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is not configured.");
        }

        IDatabase database = _redis.GetDatabase();
        RedisValue[] members = await database.SetMembersAsync(RegistryIndexKey)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        string[] engineIds = members
            .Where(member => !member.IsNullOrEmpty)
            .Select(member => member.ToString())
            .Where(engineId => !string.IsNullOrWhiteSpace(engineId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (engineIds.Length == 0)
        {
            return [];
        }

        RedisKey[] keys = engineIds
            .Select(engineId => (RedisKey)$"engine:registry:{engineId}")
            .ToArray();
        RedisValue[] payloads = await database.StringGetAsync(keys)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        DateTime utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        List<EngineRegistryEntry> healthyEngines = new(engineIds.Length);
        List<RedisValue> staleMembers = [];

        for (int index = 0; index < engineIds.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedisValue payload = payloads[index];
            if (payload.IsNullOrEmpty)
            {
                staleMembers.Add(engineIds[index]);
                continue;
            }

            try
            {
                EngineRegistryEntry? entry = JsonSerializer.Deserialize<EngineRegistryEntry>(
                    payload.ToString(), JsonOptions);
                if (!IsValid(entry, engineIds[index]))
                {
                    staleMembers.Add(engineIds[index]);
                    continue;
                }

                TimeSpan heartbeatAge = utcNow - entry!.LastHeartbeat.ToUniversalTime();
                if (heartbeatAge > _settings.HeartbeatStaleAfter)
                {
                    RouterLog.EngineHeartbeatStale(_logger, entry.EngineId, heartbeatAge.TotalSeconds);
                    staleMembers.Add(engineIds[index]);
                    continue;
                }

                entry.Intents = NormalizeTags(entry.Intents);
                healthyEngines.Add(entry);
            }
            catch (JsonException ex)
            {
                RouterLog.EngineEntryDeserializationFailed(_logger, ex, keys[index]);
                staleMembers.Add(engineIds[index]);
            }
        }

        if (staleMembers.Count > 0)
        {
            await RemoveStaleMembersAsync(database, staleMembers.ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }

        return healthyEngines
            .OrderBy(entry => entry.EngineId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task RemoveStaleMembersAsync(
        IDatabase database,
        RedisValue[] members,
        CancellationToken cancellationToken)
    {
        try
        {
            await database.SetRemoveAsync(RegistryIndexKey, members)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RouterLog.RegistryIndexCleanupFailed(_logger, ex, members.Length);
        }
    }

    private bool CanUseLastKnownSnapshot()
    {
        if (_settings.RedisFailureMode != RedisDiscoveryFailureMode.LastKnown ||
            _lastSuccessfulRefresh is not DateTimeOffset refreshedAt)
        {
            return false;
        }

        return _timeProvider.GetUtcNow() - refreshedAt <= _settings.SnapshotMaxAge;
    }

    private static bool IsValid(EngineRegistryEntry? entry, string expectedEngineId) =>
        entry != null &&
        string.Equals(entry.EngineId, expectedEngineId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(entry.Host) &&
        entry.Port is > 0 and <= 65535;

    private static string[] NormalizeTags(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
}
