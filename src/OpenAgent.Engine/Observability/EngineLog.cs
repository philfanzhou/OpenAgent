using Microsoft.Extensions.Logging;

namespace OpenAgent.Engine.Observability;

internal static partial class EngineLog
{
    [LoggerMessage(EventId = 4079, Level = LogLevel.Warning, Message = "Configuration cache operation failed. Operation={Operation}, Key={Key}, ExceptionType={ExceptionType}")]
    public static partial void ConfigurationCacheFailed(ILogger logger, string operation, string key, string exceptionType);

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

    [LoggerMessage(EventId = 4040, Level = LogLevel.Debug, Message = "{RegistrarName} registrar: Redis not available. Skipping registration.")]
    public static partial void RedisRegistrarSkipped(ILogger logger, string registrarName);

    public static void RedisRegistrarIndexReadFailed(ILogger logger, Exception exception, string registrarName) =>
        RedisRegistrarIndexReadFailedCore(logger, exception, registrarName, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4041, Level = LogLevel.Warning, Message = "{RegistrarName} registrar: Failed to read published index from Redis. ExceptionType={ExceptionType}")]
    private static partial void RedisRegistrarIndexReadFailedCore(ILogger logger, Exception exception, string registrarName, string exceptionType);

    [LoggerMessage(EventId = 4042, Level = LogLevel.Information, Message = "{RegistrarName} registrar: No entries found in published index.")]
    public static partial void RedisRegistrarNoneFound(ILogger logger, string registrarName);

    [LoggerMessage(EventId = 4043, Level = LogLevel.Debug, Message = "{RegistrarName} registrar: Registered entry '{EntryId}' from Redis")]
    public static partial void RedisRegistrarRegistered(ILogger logger, string registrarName, string entryId);

    public static void RedisRegistrarRegisterFailed(ILogger logger, Exception exception, string registrarName, string entryId) =>
        RedisRegistrarRegisterFailedCore(logger, exception, registrarName, entryId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4044, Level = LogLevel.Error, Message = "{RegistrarName} registrar: Failed to register entry '{EntryId}'. ExceptionType={ExceptionType}")]
    private static partial void RedisRegistrarRegisterFailedCore(ILogger logger, Exception exception, string registrarName, string entryId, string exceptionType);

    [LoggerMessage(EventId = 4045, Level = LogLevel.Information, Message = "{RegistrarName} registrar: {Count} entries registered from Redis.")]
    public static partial void RedisRegistrarComplete(ILogger logger, string registrarName, int count);

    [LoggerMessage(EventId = 4046, Level = LogLevel.Information, Message = "Engine registered with ID: {EngineId} at {Host}:{Port}")]
    public static partial void EngineRegistered(ILogger logger, string engineId, string host, int port);

    [LoggerMessage(EventId = 4047, Level = LogLevel.Warning, Message = "Failed to register engine in Redis. StringSetAsync returned false.")]
    public static partial void EngineRegisterStringSetFailed(ILogger logger);

    public static void EngineRegisterFailed(ILogger logger, Exception exception) =>
        EngineRegisterFailedCore(logger, exception, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4048, Level = LogLevel.Warning, Message = "Failed to register engine in Redis. Continuing in island mode. ExceptionType={ExceptionType}")]
    private static partial void EngineRegisterFailedCore(ILogger logger, Exception exception, string exceptionType);

    public static void HeartbeatSendFailed(ILogger logger, Exception exception) =>
        HeartbeatSendFailedCore(logger, exception, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4049, Level = LogLevel.Warning, Message = "Failed to send heartbeat to Redis. ExceptionType={ExceptionType}")]
    private static partial void HeartbeatSendFailedCore(ILogger logger, Exception exception, string exceptionType);

    [LoggerMessage(EventId = 4050, Level = LogLevel.Information, Message = "Engine deregistered from Redis. ID: {EngineId}")]
    public static partial void EngineDeregisteredFromRedis(ILogger logger, string engineId);

    public static void EngineDeregisterFailed(ILogger logger, Exception exception) =>
        EngineDeregisterFailedCore(logger, exception, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4051, Level = LogLevel.Warning, Message = "Failed to deregister engine in Redis. TTL will expire naturally. ExceptionType={ExceptionType}")]
    private static partial void EngineDeregisterFailedCore(ILogger logger, Exception exception, string exceptionType);

    [LoggerMessage(EventId = 4052, Level = LogLevel.Information, Message = "Detected listening port after app start: {Port}")]
    public static partial void PortDetected(ILogger logger, int port);

    [LoggerMessage(EventId = 4053, Level = LogLevel.Information, Message = "Engine registered after port detection.")]
    public static partial void EngineRegisteredAfterPortDetection(ILogger logger);

    public static void InitialRegistrationFailed(ILogger logger, Exception exception) =>
        InitialRegistrationFailedCore(logger, exception, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4054, Level = LogLevel.Warning, Message = "Initial registration after port detection failed. ExceptionType={ExceptionType}")]
    private static partial void InitialRegistrationFailedCore(ILogger logger, Exception exception, string exceptionType);

    [LoggerMessage(EventId = 4055, Level = LogLevel.Information, Message = "Engine heartbeat service starting...")]
    public static partial void HeartbeatServiceStarting(ILogger logger);

    [LoggerMessage(EventId = 4056, Level = LogLevel.Information, Message = "Engine not registered. Attempting to register...")]
    public static partial void HeartbeatRetryingRegistration(ILogger logger);

    public static void HeartbeatFailed(ILogger logger, Exception exception) =>
        HeartbeatFailedCore(logger, exception, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4057, Level = LogLevel.Warning, Message = "Heartbeat failed, will retry. ExceptionType={ExceptionType}")]
    private static partial void HeartbeatFailedCore(ILogger logger, Exception exception, string exceptionType);

    [LoggerMessage(EventId = 4058, Level = LogLevel.Information, Message = "Engine heartbeat service stopped.")]
    public static partial void HeartbeatServiceStopped(ILogger logger);

    public static void StreamingHeartbeatFailed(ILogger logger, Exception exception, string endpoint, string traceId) =>
        StreamingHeartbeatFailedCore(logger, exception, endpoint, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4060, Level = LogLevel.Warning, Message = "Streaming heartbeat failed. Endpoint={Endpoint}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void StreamingHeartbeatFailedCore(ILogger logger, Exception exception, string endpoint, string traceId, string exceptionType);

    public static void UnhandledExceptionAfterResponseStart(ILogger logger, Exception exception, string method, string path, string traceId) =>
        UnhandledExceptionAfterResponseStartCore(logger, exception, method, path, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4061, Level = LogLevel.Warning, Message = "Unhandled exception after response started. Method={Method}, Path={Path}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void UnhandledExceptionAfterResponseStartCore(ILogger logger, Exception exception, string method, string path, string traceId, string exceptionType);

    public static void UnhandledExceptionMappedToProblemDetails(ILogger logger, Exception exception, string method, string path, int statusCode, string traceId) =>
        UnhandledExceptionMappedToProblemDetailsCore(logger, exception, method, path, statusCode, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4062, Level = LogLevel.Error, Message = "Unhandled exception mapped to problem details. Method={Method}, Path={Path}, StatusCode={StatusCode}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void UnhandledExceptionMappedToProblemDetailsCore(ILogger logger, Exception exception, string method, string path, int statusCode, string traceId, string exceptionType);

    public static void SseEndpointErrorOccurred(ILogger logger, Exception exception, string method, string path, string traceId, bool responseStarted) =>
        SseEndpointErrorOccurredCore(logger, exception, method, path, traceId, responseStarted, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4063, Level = LogLevel.Error, Message = "SSE endpoint error occurred. Method={Method}, Path={Path}, TraceId={TraceId}, ResponseStarted={ResponseStarted}, ExceptionType={ExceptionType}")]
    private static partial void SseEndpointErrorOccurredCore(ILogger logger, Exception exception, string method, string path, string traceId, bool responseStarted, string exceptionType);

    public static void LlmConfigCacheEvictionFailed(ILogger logger, Exception exception, string profileId) =>
        LlmConfigCacheEvictionFailedCore(logger, exception, profileId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 4078, Level = LogLevel.Warning, Message = "PostgreSQL deleted LLM configuration but Redis cache eviction failed. ProfileId={ProfileId}, ExceptionType={ExceptionType}")]
    private static partial void LlmConfigCacheEvictionFailedCore(ILogger logger, Exception exception, string profileId, string exceptionType);
}
