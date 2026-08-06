using OpenAgent.Contracts.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Router.Observability;
using StackExchange.Redis;

namespace OpenAgent.Router;

public class RedisServiceDiscoveryRouteTable : IRouteTable
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly EngineRegistrySnapshotCache _snapshotCache;
    private readonly ILogger<RedisServiceDiscoveryRouteTable> _logger;
    private readonly IConsistentHashRing _hashRing;

    public RedisServiceDiscoveryRouteTable(
        IConnectionMultiplexer? redis,
        ILogger<RedisServiceDiscoveryRouteTable> logger,
        IConsistentHashRing hashRing)
        : this(redis, new EngineRegistrySnapshotCache(redis, NullLogger<EngineRegistrySnapshotCache>.Instance), logger, hashRing)
    {
    }

    public RedisServiceDiscoveryRouteTable(
        IConnectionMultiplexer? redis,
        EngineRegistrySnapshotCache snapshotCache,
        ILogger<RedisServiceDiscoveryRouteTable> logger,
        IConsistentHashRing hashRing)
    {
        _redis = redis;
        _snapshotCache = snapshotCache;
        _logger = logger;
        _hashRing = hashRing;
    }

    public string? GetTargetEndpoint(string intent)
    {
        return GetTargetEndpoint(intent, tenantId: null, conversationId: null);
    }

    public string? GetTargetEndpoint(string intent, string? tenantId, string? conversationId)
    {
        if (_redis == null)
        {
            RouterLog.RedisNotAvailableForDiscovery(_logger);
            return null;
        }

        try
        {
            var healthyEngines = _snapshotCache.Snapshot;

            if (healthyEngines.Count == 0)
            {
                RouterLog.NoHealthyEnginesInSnapshot(_logger);
                return null;
            }

            // Update hash ring with current engine IDs
            var engineIds = healthyEngines.Select(e => e.EngineId).ToList();
            _hashRing.UpdateNodes(engineIds);

            EngineRegistryEntry? selectedEngine = null;

            // Session affinity: if conversationId is provided, try to route to the same engine
            if (!string.IsNullOrEmpty(conversationId))
            {
                var hashKey = !string.IsNullOrEmpty(tenantId)
                    ? $"{tenantId}:{conversationId}"
                    : conversationId;
                var targetEngineId = _hashRing.GetNode(hashKey);
                if (targetEngineId != null)
                {
                    selectedEngine = healthyEngines.FirstOrDefault(e => e.EngineId == targetEngineId);
                    if (selectedEngine != null)
                    {
                        RouterLog.SessionAffinityEngineSelected(_logger, selectedEngine.EngineId, conversationId);
                    }
                    else
                    {
                        RouterLog.AffinityEngineNotInHealthyList(_logger, targetEngineId);
                    }
                }
            }

            // Fallback: select lowest load engine
            if (selectedEngine == null)
            {
                foreach (var entry in healthyEngines)
                {
                    if (selectedEngine == null || entry.Load < selectedEngine.Load)
                    {
                        selectedEngine = entry;
                    }
                }
            }

            if (selectedEngine == null)
            {
                RouterLog.NoHealthyEnginesInRegistry(_logger);
                return null;
            }

            var endpoint = $"http://{selectedEngine.Host}:{selectedEngine.Port}";
            RouterLog.EngineSelected(_logger, selectedEngine.EngineId, endpoint, selectedEngine.Load);
            return endpoint;
        }
        catch (Exception ex)
        {
            RouterLog.DiscoveryUnexpectedError(_logger, ex);
            return null;
        }
    }
}
