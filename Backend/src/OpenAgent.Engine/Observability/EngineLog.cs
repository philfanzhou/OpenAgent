using Microsoft.Extensions.Logging;

namespace OpenAgent.Engine.Observability;

internal static partial class EngineLog
{
    // --- ConfigProvider ---

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "No AgentId provided. Degrading to MockAgent (AllowMockAgent=true).")]
    public static partial void ConfigMockFallback(ILogger logger);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "No AgentId provided and AllowMockAgent is disabled.")]
    public static partial void ConfigMissingAgentIdDisabled(ILogger logger);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Debug, Message = "Agent config loaded from in-memory snapshot for agent {AgentId}")]
    public static partial void ConfigLoadedFromSnapshot(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information, Message = "Agent config loaded from Redis and cached for agent {AgentId}")]
    public static partial void ConfigLoadedFromRedisCached(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning, Message = "Redis is not available. Entering island mode — skipping Redis config lookup for agent {AgentId}.")]
    public static partial void ConfigRedisIslandMode(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Information, Message = "No config found for agent {AgentId}. Degrading to MockAgent (AllowMockAgent=true).")]
    public static partial void ConfigNotFoundDegradingToMock(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "No cached configuration available for agent {AgentId}.")]
    public static partial void ConfigNotCached(ILogger logger, string agentId);

    public static void ConfigSnapshotLoadFailed(ILogger logger, Exception ex, string agentId) =>
        ConfigSnapshotLoadFailedCore(logger, ex, agentId, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4007, Level = LogLevel.Warning, Message = "Failed to load config from snapshot for agent {AgentId}. ExceptionType={ExceptionType}")]
    private static partial void ConfigSnapshotLoadFailedCore(ILogger logger, Exception ex, string agentId, string exceptionType);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Debug, Message = "Agent config not found in Redis for agent: {AgentId}")]
    public static partial void ConfigNotFoundInRedis(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Information, Message = "Loaded agent config from Redis. AgentId={AgentId}, Version={Version}, ApiFormat={ApiFormat}, PayloadBytes={PayloadBytes}")]
    public static partial void ConfigLoadedFromRedisDetails(ILogger logger, string agentId, string? version, string apiFormat, int payloadBytes);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Warning, Message = "Agent config payload from Redis did not contain a usable config. AgentId={AgentId}, PayloadBytes={PayloadBytes}")]
    public static partial void ConfigPayloadInvalid(ILogger logger, string agentId, int payloadBytes);

    public static void ConfigDeserializeFailed(ILogger logger, Exception ex, string agentId, int payloadBytes) =>
        ConfigDeserializeFailedCore(logger, ex, agentId, payloadBytes, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4011, Level = LogLevel.Error, Message = "Failed to deserialize agent config from Redis. AgentId={AgentId}, PayloadBytes={PayloadBytes}, ExceptionType={ExceptionType}")]
    private static partial void ConfigDeserializeFailedCore(ILogger logger, Exception ex, string agentId, int payloadBytes, string exceptionType);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Warning, Message = "Redis is not available. Cannot list agents.")]
    public static partial void ListAgentsRedisUnavailable(ILogger logger);

    public static void ListAgentsParseFailed(ILogger logger, Exception ex, string? agentId, int payloadBytes) =>
        ListAgentsParseFailedCore(logger, ex, agentId, payloadBytes, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4013, Level = LogLevel.Warning, Message = "Failed to parse agent config while listing agents. AgentId={AgentId}, PayloadBytes={PayloadBytes}, ExceptionType={ExceptionType}")]
    private static partial void ListAgentsParseFailedCore(ILogger logger, Exception ex, string? agentId, int payloadBytes, string exceptionType);

    public static void ListAgentsFailed(ILogger logger, Exception ex) => ListAgentsFailedCore(logger, ex, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4014, Level = LogLevel.Error, Message = "Failed to list agents from Redis. ExceptionType={ExceptionType}")]
    private static partial void ListAgentsFailedCore(ILogger logger, Exception ex, string exceptionType);

    // --- ConfigSnapshot ---

    [LoggerMessage(EventId = 4015, Level = LogLevel.Warning, Message = "ConfigSnapshot.Clear() fallback: IMemoryCache is not a strong MemoryCache; clear is best-effort.")]
    public static partial void ConfigSnapshotClearFallback(ILogger logger);

    // --- HotReloadService ---

    [LoggerMessage(EventId = 4016, Level = LogLevel.Information, Message = "Subscribed to config updates. Waiting for cancellation...")]
    public static partial void HotReloadSubscribed(ILogger logger);

    [LoggerMessage(EventId = 4017, Level = LogLevel.Information, Message = "Subscribing to config updates on channel: {Channel}")]
    public static partial void HotReloadSubscribingChannel(ILogger logger, string channel);

    public static void HotReloadProcessMessageError(ILogger logger, Exception ex, string channel) =>
        HotReloadProcessMessageErrorCore(logger, ex, channel, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4018, Level = LogLevel.Warning, Message = "Error processing config update message from channel {Channel}. ExceptionType={ExceptionType}")]
    private static partial void HotReloadProcessMessageErrorCore(ILogger logger, Exception ex, string channel, string exceptionType);

    [LoggerMessage(EventId = 4019, Level = LogLevel.Information, Message = "Received config update on {Channel}: {Message}")]
    public static partial void HotReloadMessageReceived(ILogger logger, string channel, string message);

    [LoggerMessage(EventId = 4020, Level = LogLevel.Warning, Message = "Ignoring empty config update payload on channel {Channel}")]
    public static partial void HotReloadEmptyPayloadIgnored(ILogger logger, string channel);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Warning, Message = "Failed to parse config update: null result")]
    public static partial void HotReloadParseNullResult(ILogger logger);

    [LoggerMessage(EventId = 4022, Level = LogLevel.Warning, Message = "Config update missing AgentId, ignoring message")]
    public static partial void HotReloadMissingAgentId(ILogger logger);

    [LoggerMessage(EventId = 4023, Level = LogLevel.Information, Message = "Reloaded full agent config from Redis after config update notification. Agent: {AgentId}")]
    public static partial void HotReloadFullConfigReloaded(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4024, Level = LogLevel.Information, Message = "Received full sync notification. Cleared all in-memory config snapshots.")]
    public static partial void HotReloadFullSyncSnapshotCleared(ILogger logger);

    public static void HotReloadProcessError(ILogger logger, Exception ex, string message) =>
        HotReloadProcessErrorCore(logger, ex, message, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4025, Level = LogLevel.Error, Message = "Error processing config message: {Message}. ExceptionType={ExceptionType}")]
    private static partial void HotReloadProcessErrorCore(ILogger logger, Exception ex, string message, string exceptionType);

    [LoggerMessage(EventId = 4026, Level = LogLevel.Warning, Message = "Ignoring blank legacy config update on channel {Channel}")]
    public static partial void HotReloadLegacyBlankPayloadIgnored(ILogger logger, string channel);

    [LoggerMessage(EventId = 4027, Level = LogLevel.Information, Message = "Refreshed full config snapshot from legacy notification. Channel: {Channel}, Agent: {AgentId}")]
    public static partial void HotReloadLegacyRefreshed(ILogger logger, string channel, string agentId);

    [LoggerMessage(EventId = 4028, Level = LogLevel.Information, Message = "Received legacy registry notification on channel {Channel} with payload {Payload}. No direct snapshot mutation is required.")]
    public static partial void HotReloadLegacyNotificationReceived(ILogger logger, string channel, string payload);

    [LoggerMessage(EventId = 4029, Level = LogLevel.Warning, Message = "Redis notification received for agent {AgentId}, but no config exists in Redis")]
    public static partial void HotReloadRefreshNoConfig(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4030, Level = LogLevel.Warning, Message = "Redis config payload for agent {AgentId} did not contain a valid runtime config")]
    public static partial void HotReloadRefreshInvalidPayload(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4031, Level = LogLevel.Information, Message = "Refreshed full config snapshot from Redis. Agent: {AgentId}, CurrentVersion: {CurrentVersion}")]
    public static partial void HotReloadRefreshCompleted(ILogger logger, string agentId, string? currentVersion);

    public static void HotReloadRefreshFailed(ILogger logger, Exception ex, string agentId) =>
        HotReloadRefreshFailedCore(logger, ex, agentId, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4032, Level = LogLevel.Error, Message = "Failed to refresh config from Redis for agent {AgentId}. ExceptionType={ExceptionType}")]
    private static partial void HotReloadRefreshFailedCore(ILogger logger, Exception ex, string agentId, string exceptionType);

    [LoggerMessage(EventId = 4033, Level = LogLevel.Debug, Message = "Redis is unavailable. Hot reload subscription will retry.")]
    public static partial void HotReloadRedisUnavailable(ILogger logger);

    // --- ShutdownService ---

    [LoggerMessage(EventId = 4034, Level = LogLevel.Information, Message = "Initiating graceful shutdown with timeout: {TimeoutSeconds}s")]
    public static partial void ShutdownInitiated(ILogger logger, double timeoutSeconds);

    [LoggerMessage(EventId = 4035, Level = LogLevel.Information, Message = "Waiting for {RemainingRequests} in-flight requests to complete...")]
    public static partial void ShutdownWaitingForRequests(ILogger logger, int remainingRequests);

    [LoggerMessage(EventId = 4036, Level = LogLevel.Debug, Message = "Pending request: {RequestId}, Type: {RequestType}, Duration: {DurationMs}ms")]
    public static partial void ShutdownPendingRequest(ILogger logger, string requestId, string requestType, double durationMs);

    [LoggerMessage(EventId = 4037, Level = LogLevel.Warning, Message = "Shutdown timeout reached. {RemainingRequests} requests are still running and may be terminated by the host")]
    public static partial void ShutdownTimeoutReached(ILogger logger, int remainingRequests);

    [LoggerMessage(EventId = 4038, Level = LogLevel.Warning, Message = "Request still running at shutdown timeout: {RequestId}, Type: {RequestType}, Duration: {DurationMs}ms")]
    public static partial void ShutdownTimeoutRunningRequest(ILogger logger, string requestId, string requestType, double durationMs);

    [LoggerMessage(EventId = 4039, Level = LogLevel.Information, Message = "Graceful shutdown completed successfully. All requests finished in {DurationMs}ms")]
    public static partial void ShutdownCompleted(ILogger logger, long durationMs);

    // --- Redis Registrars — shared ---

    [LoggerMessage(EventId = 4040, Level = LogLevel.Debug, Message = "{RegistrarName} registrar: Redis not available. Skipping registration.")]
    public static partial void RedisRegistrarSkipped(ILogger logger, string registrarName);

    public static void RedisRegistrarIndexReadFailed(ILogger logger, Exception ex, string registrarName) =>
        RedisRegistrarIndexReadFailedCore(logger, ex, registrarName, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4041, Level = LogLevel.Warning, Message = "{RegistrarName} registrar: Failed to read published index from Redis. ExceptionType={ExceptionType}")]
    private static partial void RedisRegistrarIndexReadFailedCore(ILogger logger, Exception ex, string registrarName, string exceptionType);

    [LoggerMessage(EventId = 4042, Level = LogLevel.Information, Message = "{RegistrarName} registrar: No entries found in published index.")]
    public static partial void RedisRegistrarNoneFound(ILogger logger, string registrarName);

    [LoggerMessage(EventId = 4043, Level = LogLevel.Debug, Message = "{RegistrarName} registrar: Registered entry '{EntryId}' from Redis")]
    public static partial void RedisRegistrarRegistered(ILogger logger, string registrarName, string entryId);

    public static void RedisRegistrarRegisterFailed(ILogger logger, Exception ex, string registrarName, string entryId) =>
        RedisRegistrarRegisterFailedCore(logger, ex, registrarName, entryId, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4044, Level = LogLevel.Error, Message = "{RegistrarName} registrar: Failed to register entry '{EntryId}'. ExceptionType={ExceptionType}")]
    private static partial void RedisRegistrarRegisterFailedCore(ILogger logger, Exception ex, string registrarName, string entryId, string exceptionType);

    [LoggerMessage(EventId = 4045, Level = LogLevel.Information, Message = "{RegistrarName} registrar: {Count} entries registered from Redis.")]
    public static partial void RedisRegistrarComplete(ILogger logger, string registrarName, int count);

    // --- RedisRegistry ---

    [LoggerMessage(EventId = 4046, Level = LogLevel.Information, Message = "Engine registered with ID: {EngineId} at {Host}:{Port}")]
    public static partial void EngineRegistered(ILogger logger, string engineId, string host, int port);

    [LoggerMessage(EventId = 4047, Level = LogLevel.Warning, Message = "Failed to register engine in Redis. StringSetAsync returned false.")]
    public static partial void EngineRegisterStringSetFailed(ILogger logger);

    public static void EngineRegisterFailed(ILogger logger, Exception ex) =>
        EngineRegisterFailedCore(logger, ex, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4048, Level = LogLevel.Warning, Message = "Failed to register engine in Redis. Continuing in island mode. ExceptionType={ExceptionType}")]
    private static partial void EngineRegisterFailedCore(ILogger logger, Exception ex, string exceptionType);

    public static void HeartbeatSendFailed(ILogger logger, Exception ex) =>
        HeartbeatSendFailedCore(logger, ex, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4049, Level = LogLevel.Warning, Message = "Failed to send heartbeat to Redis. ExceptionType={ExceptionType}")]
    private static partial void HeartbeatSendFailedCore(ILogger logger, Exception ex, string exceptionType);

    [LoggerMessage(EventId = 4050, Level = LogLevel.Information, Message = "Engine deregistered from Redis. ID: {EngineId}")]
    public static partial void EngineDeregisteredFromRedis(ILogger logger, string engineId);

    public static void EngineDeregisterFailed(ILogger logger, Exception ex) =>
        EngineDeregisterFailedCore(logger, ex, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4051, Level = LogLevel.Warning, Message = "Failed to deregister engine from Redis. TTL will expire naturally. ExceptionType={ExceptionType}")]
    private static partial void EngineDeregisterFailedCore(ILogger logger, Exception ex, string exceptionType);

    // --- HeartbeatService ---

    [LoggerMessage(EventId = 4052, Level = LogLevel.Information, Message = "Detected listening port after app start: {Port}")]
    public static partial void PortDetected(ILogger logger, int port);

    [LoggerMessage(EventId = 4053, Level = LogLevel.Information, Message = "Engine registered after port detection.")]
    public static partial void EngineRegisteredAfterPortDetection(ILogger logger);

    public static void InitialRegistrationFailed(ILogger logger, Exception ex) =>
        InitialRegistrationFailedCore(logger, ex, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4054, Level = LogLevel.Warning, Message = "Initial registration after port detection failed. ExceptionType={ExceptionType}")]
    private static partial void InitialRegistrationFailedCore(ILogger logger, Exception ex, string exceptionType);

    [LoggerMessage(EventId = 4055, Level = LogLevel.Information, Message = "Engine heartbeat service starting...")]
    public static partial void HeartbeatServiceStarting(ILogger logger);

    [LoggerMessage(EventId = 4056, Level = LogLevel.Information, Message = "Engine not registered. Attempting to register...")]
    public static partial void HeartbeatRetryingRegistration(ILogger logger);

    public static void HeartbeatFailed(ILogger logger, Exception ex) =>
        HeartbeatFailedCore(logger, ex, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4057, Level = LogLevel.Warning, Message = "Heartbeat failed, will retry... ExceptionType={ExceptionType}")]
    private static partial void HeartbeatFailedCore(ILogger logger, Exception ex, string exceptionType);

    [LoggerMessage(EventId = 4058, Level = LogLevel.Information, Message = "Engine heartbeat service stopped.")]
    public static partial void HeartbeatServiceStopped(ILogger logger);

    // --- Health Checks & Streaming ---

    public static void LlmHealthCheckFailed(ILogger logger, Exception ex) =>
        LlmHealthCheckFailedCore(logger, ex, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4059, Level = LogLevel.Warning, Message = "LLM health check failed. ExceptionType={ExceptionType}")]
    private static partial void LlmHealthCheckFailedCore(ILogger logger, Exception ex, string exceptionType);

    public static void StreamingHeartbeatFailed(ILogger logger, Exception ex, string endpoint, string traceId) =>
        StreamingHeartbeatFailedCore(logger, ex, endpoint, traceId, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4060, Level = LogLevel.Warning, Message = "Streaming heartbeat failed. Endpoint={Endpoint}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void StreamingHeartbeatFailedCore(ILogger logger, Exception ex, string endpoint, string traceId, string exceptionType);

    // --- Middleware ---

    public static void UnhandledExceptionAfterResponseStart(ILogger logger, Exception ex, string method, string path, string traceId) =>
        UnhandledExceptionAfterResponseStartCore(logger, ex, method, path, traceId, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4061, Level = LogLevel.Warning, Message = "Unhandled exception after response started. Method={Method}, Path={Path}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void UnhandledExceptionAfterResponseStartCore(ILogger logger, Exception ex, string method, string path, string traceId, string exceptionType);

    public static void UnhandledExceptionMappedToProblemDetails(ILogger logger, Exception ex, string method, string path, int statusCode, string traceId) =>
        UnhandledExceptionMappedToProblemDetailsCore(logger, ex, method, path, statusCode, traceId, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4062, Level = LogLevel.Error, Message = "Unhandled exception mapped to problem details. Method={Method}, Path={Path}, StatusCode={StatusCode}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void UnhandledExceptionMappedToProblemDetailsCore(ILogger logger, Exception ex, string method, string path, int statusCode, string traceId, string exceptionType);

    public static void SseEndpointErrorOccurred(ILogger logger, Exception ex, string method, string path, string traceId, bool responseStarted) =>
        SseEndpointErrorOccurredCore(logger, ex, method, path, traceId, responseStarted, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4063, Level = LogLevel.Error, Message = "SSE endpoint error occurred. Method={Method}, Path={Path}, TraceId={TraceId}, ResponseStarted={ResponseStarted}, ExceptionType={ExceptionType}")]
    private static partial void SseEndpointErrorOccurredCore(ILogger logger, Exception ex, string method, string path, string traceId, bool responseStarted, string exceptionType);

    [LoggerMessage(EventId = 4064, Level = LogLevel.Warning, Message = "Config update for resource type {ResourceType} is missing ResourceId.")]
    public static partial void HotReloadMissingResourceId(ILogger logger, string resourceType);

    [LoggerMessage(EventId = 4065, Level = LogLevel.Warning, Message = "Ignoring config update with unknown resource type {ResourceType}.")]
    public static partial void HotReloadUnknownResourceType(ILogger logger, string resourceType);

    [LoggerMessage(EventId = 4066, Level = LogLevel.Information, Message = "Reloaded LLM profile {ProfileId} from Redis after config update notification.")]
    public static partial void HotReloadLlmProfileRefreshed(ILogger logger, string profileId);

    [LoggerMessage(EventId = 4067, Level = LogLevel.Information, Message = "Removed LLM profile {ProfileId} after config update notification.")]
    public static partial void HotReloadLlmProfileRemoved(ILogger logger, string profileId);

    [LoggerMessage(EventId = 4068, Level = LogLevel.Warning, Message = "Redis payload for LLM profile {ProfileId} is invalid.")]
    public static partial void HotReloadLlmProfileInvalid(ILogger logger, string profileId);

    public static void HotReloadLlmProfileRefreshFailed(ILogger logger, Exception ex, string profileId) =>
        HotReloadLlmProfileRefreshFailedCore(logger, ex, profileId, ex.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4069, Level = LogLevel.Error, Message = "Failed to reload LLM profile {ProfileId} from Redis. ExceptionType={ExceptionType}")]
    private static partial void HotReloadLlmProfileRefreshFailedCore(ILogger logger, Exception ex, string profileId, string exceptionType);

    // --- PostgreSQL Agent configuration proof of concept ---

    [LoggerMessage(EventId = 4070, Level = LogLevel.Information, Message = "Loaded Agent configuration from PostgreSQL. AgentId={AgentId}, Version={Version}")]
    public static partial void AgentConfigLoadedFromPostgreSql(
        ILogger logger,
        string agentId,
        string version);

    public static void AgentConfigCacheReadFailed(
        ILogger logger,
        Exception exception,
        string agentId) => AgentConfigCacheReadFailedCore(
            logger,
            exception,
            agentId,
            exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4071, Level = LogLevel.Warning, Message = "Failed to read the PostgreSQL-derived Agent configuration cache. AgentId={AgentId}, ExceptionType={ExceptionType}")]
    private static partial void AgentConfigCacheReadFailedCore(
        ILogger logger,
        Exception exception,
        string agentId,
        string exceptionType);

    public static void AgentConfigCacheWriteFailed(
        ILogger logger,
        Exception exception,
        string agentId) => AgentConfigCacheWriteFailedCore(
            logger,
            exception,
            agentId,
            exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4072, Level = LogLevel.Warning, Message = "PostgreSQL committed Agent configuration but Redis cache refresh failed. AgentId={AgentId}, ExceptionType={ExceptionType}")]
    private static partial void AgentConfigCacheWriteFailedCore(
        ILogger logger,
        Exception exception,
        string agentId,
        string exceptionType);

    [LoggerMessage(EventId = 4073, Level = LogLevel.Information, Message = "Refreshed Agent configuration from the PostgreSQL-derived Redis cache. AgentId={AgentId}, Version={Version}")]
    public static partial void AgentConfigPostgreSqlHotReloaded(
        ILogger logger,
        string agentId,
        string version);

    [LoggerMessage(EventId = 4074, Level = LogLevel.Information, Message = "PostgreSQL Agent configuration cache warmup completed. Cached={CachedCount}, Total={TotalCount}")]
    public static partial void AgentConfigCacheWarmupCompleted(
        ILogger logger,
        int cachedCount,
        int totalCount);

    public static void AgentConfigCacheWarmupFailed(
        ILogger logger,
        Exception exception) => AgentConfigCacheWarmupFailedCore(
            logger,
            exception,
            exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4075, Level = LogLevel.Warning, Message = "PostgreSQL Agent configuration cache warmup failed and will retry. ExceptionType={ExceptionType}")]
    private static partial void AgentConfigCacheWarmupFailedCore(
        ILogger logger,
        Exception exception,
        string exceptionType);

    [LoggerMessage(EventId = 4076, Level = LogLevel.Warning, Message = "Redis rejected the PostgreSQL-derived Agent configuration cache write. AgentId={AgentId}")]
    public static partial void AgentConfigCacheWriteRejected(ILogger logger, string agentId);

    // --- Host Lifecycle / Program.cs ---

    // --- Endpoint Extensions ---

}
