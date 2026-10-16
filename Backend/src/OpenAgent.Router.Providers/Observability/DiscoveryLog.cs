using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenAgent.Router.Observability;

/// <summary>
/// Logging definitions for Router service discovery, route tables and downstream readiness.
/// EventId values are shared with <see cref="RouterLog"/> in the 3000-3034 range.
/// </summary>
internal static partial class DiscoveryLog
{
    [LoggerMessage(EventId = 3012, Level = LogLevel.Debug, Message = "Session affinity selected engine {EngineId} for conversation {ConversationId}")]
    public static partial void SessionAffinityEngineSelected(ILogger logger, string engineId, string conversationId);

    [LoggerMessage(EventId = 3015, Level = LogLevel.Information, Message = "Selected engine {EngineId} at {Endpoint} with load {Load}")]
    public static partial void EngineSelected(ILogger logger, string engineId, string endpoint, int load);

    [LoggerMessage(EventId = 3016, Level = LogLevel.Warning, Message = "Unexpected error during service discovery")]
    public static partial void DiscoveryUnexpectedError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3017, Level = LogLevel.Warning, Message = "Failed to refresh engine registry snapshot")]
    public static partial void RefreshSnapshotFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3018, Level = LogLevel.Warning, Message = "Failed to refresh engine registry snapshot. RedisFailureMode={RedisFailureMode}")]
    public static partial void RefreshSnapshotUnavailable(ILogger logger, Exception exception, string redisFailureMode);

    [LoggerMessage(EventId = 3019, Level = LogLevel.Warning, Message = "Engine {EngineId} heartbeat is stale ({HeartbeatAge}s), skipping")]
    public static partial void EngineHeartbeatStale(ILogger logger, string engineId, double heartbeatAge);

    [LoggerMessage(EventId = 3020, Level = LogLevel.Warning, Message = "Failed to deserialize engine registry entry for key {Key}")]
    public static partial void EngineEntryDeserializationFailed(ILogger logger, Exception exception, RedisKey key);

    [LoggerMessage(EventId = 3021, Level = LogLevel.Debug, Message = "Dynamic discovery returned endpoint: {Endpoint}")]
    public static partial void DynamicDiscoveryReturnedEndpoint(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3022, Level = LogLevel.Debug, Message = "Falling back to static endpoint: {Endpoint}")]
    public static partial void FallbackToStaticEndpoint(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3023, Level = LogLevel.Warning, Message = "No endpoint found for intent: {Intent}")]
    public static partial void NoEndpointForIntent(ILogger logger, string intent);

    [LoggerMessage(EventId = 3029, Level = LogLevel.Warning, Message = "No eligible Engine supports the route. Intent={Intent}")]
    public static partial void NoEligibleEngines(ILogger logger, string intent);

    [LoggerMessage(EventId = 3030, Level = LogLevel.Warning, Message = "Failed to remove {Count} stale members from the Engine registry index")]
    public static partial void RegistryIndexCleanupFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(EventId = 3031, Level = LogLevel.Warning, Message = "Downstream Engine readiness probe failed. Endpoint={Endpoint}")]
    public static partial void DownstreamProbeFailed(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 3032, Level = LogLevel.Warning, Message = "Downstream Engine is not ready. Endpoint={Endpoint}, StatusCode={StatusCode}")]
    public static partial void DownstreamNotReady(ILogger logger, string endpoint, int statusCode);
}
