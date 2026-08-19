using OpenAgent.Contracts.Routing;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router;

internal sealed class RedisServiceDiscoveryRouteTable : IRouteTable
{
    private readonly EngineRegistrySnapshotCache _snapshotCache;
    private readonly ILogger<RedisServiceDiscoveryRouteTable> _logger;
    private readonly IConsistentHashRing _hashRing;
    private readonly IEndpointHealthTracker _healthTracker;
    private readonly object _hashLock = new();

    public RedisServiceDiscoveryRouteTable(
        EngineRegistrySnapshotCache snapshotCache,
        ILogger<RedisServiceDiscoveryRouteTable> logger,
        IConsistentHashRing hashRing,
        IEndpointHealthTracker healthTracker)
    {
        _snapshotCache = snapshotCache;
        _logger = logger;
        _hashRing = hashRing;
        _healthTracker = healthTracker;
    }

    public string? GetTargetEndpoint(string intent)
    {
        return GetTargetEndpoint(intent, tenantId: null, conversationId: null);
    }

    public string? GetTargetEndpoint(string intent, string? tenantId, string? conversationId)
    {
        try
        {
            EngineRegistryEntry[] eligibleEngines = _snapshotCache.Snapshot
                .Where(entry => Supports(entry.Intents, intent, allowEmpty: true))
                .Where(entry => _healthTracker.IsAvailable(BuildEndpoint(entry)))
                .OrderBy(entry => entry.Load)
                .ThenBy(entry => entry.EngineId, StringComparer.Ordinal)
                .ToArray();

            if (eligibleEngines.Length == 0)
            {
                RouterLog.NoEligibleEngines(_logger, intent);
                return null;
            }

            EngineRegistryEntry? selectedEngine = SelectAffinityEngine(
                eligibleEngines, tenantId, conversationId) ?? eligibleEngines[0];
            string endpoint = BuildEndpoint(selectedEngine);
            RouterLog.EngineSelected(_logger, selectedEngine.EngineId, endpoint, selectedEngine.Load);
            RouterMeter.RecordDiscoverySelection(intent, "dynamic");
            return endpoint;
        }
        catch (Exception ex)
        {
            RouterLog.DiscoveryUnexpectedError(_logger, ex);
            RouterMeter.RecordDiscoverySelection(intent, "error");
            return null;
        }
    }

    private EngineRegistryEntry? SelectAffinityEngine(
        IReadOnlyList<EngineRegistryEntry> eligibleEngines,
        string? tenantId,
        string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        string hashKey = string.IsNullOrWhiteSpace(tenantId)
            ? conversationId
            : $"{tenantId}:{conversationId}";
        string? targetEngineId;
        lock (_hashLock)
        {
            _hashRing.UpdateNodes(eligibleEngines.Select(entry => entry.EngineId));
            targetEngineId = _hashRing.GetNode(hashKey);
        }
        EngineRegistryEntry? selectedEngine = eligibleEngines.FirstOrDefault(
            entry => string.Equals(entry.EngineId, targetEngineId, StringComparison.Ordinal));
        if (selectedEngine != null)
        {
            RouterLog.SessionAffinityEngineSelected(
                _logger, selectedEngine.EngineId, conversationId);
        }

        return selectedEngine;
    }

    private static bool Supports(
        IReadOnlyCollection<string> advertisedValues,
        string? requestedValue,
        bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(requestedValue))
        {
            return true;
        }

        if (advertisedValues.Count == 0)
        {
            return allowEmpty;
        }

        return advertisedValues.Contains(requestedValue.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildEndpoint(EngineRegistryEntry entry) =>
        $"http://{entry.Host}:{entry.Port}";
}
