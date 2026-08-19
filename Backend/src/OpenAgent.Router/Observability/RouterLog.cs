using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Observability;

/// <summary>
/// Centralized logging definitions for the Router project using [LoggerMessage] source generators.
/// EventId range: 3000–3034.
/// </summary>
internal static partial class RouterLog
{
    #region --- RouterEndpointExtensions (Main routing / proxy endpoints) ---

    // Authentication / Authorization checks
    [LoggerMessage(EventId = 3000, Level = LogLevel.Warning, Message = "Request rejected: user not authenticated. Action={Action}, Method={Method}, Path={Path}, TraceId={TraceId}")]
    public static partial void UnauthenticatedRequest(ILogger logger, string? action, string method, PathString path, string? traceId);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Request rejected: tenant mismatch. Action={Action}, UserId={UserId}, UserTenantId={UserTenantId}, HeaderTenantId={HeaderTenantId}, TraceId={TraceId}")]
    public static partial void TenantMismatchRejected(ILogger logger, string? action, string userId, string? userTenantId, string headerTenantId, string? traceId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Request rejected by rate limiter. Action={Action}, ClientId={ClientId}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}")]
    public static partial void RateLimited(ILogger logger, string? action, string clientId, string userId, string? tenantId, string? traceId);

    // Idempotency cache
    [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "Idempotency cache hit. Action={Action}, IdempotencyKey={IdempotencyKey}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}")]
    public static partial void IdempotencyCacheHit(ILogger logger, string? action, string idempotencyKey, string userId, string? tenantId, string? traceId);

    public static void IdempotencyCacheCheckFailed(ILogger logger, Exception exception, string? action, string idempotencyKey, string? traceId) =>
        IdempotencyCacheCheckFailedCore(logger, exception, action, idempotencyKey, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "Idempotency cache check failed, bypassing idempotency. Action={Action}, IdempotencyKey={IdempotencyKey}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void IdempotencyCacheCheckFailedCore(ILogger logger, Exception exception, string? action, string idempotencyKey, string? traceId, string exceptionType);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "Semantic cache hit. Action={Action}, UserId={UserId}, TenantId={TenantId}, ConversationId={ConversationId}, TraceId={TraceId}")]
    public static partial void SemanticCacheHit(ILogger logger, string? action, string userId, string? tenantId, string? conversationId, string? traceId);
    /// <summary>Shared forwarding failure log used by main chat endpoint and all GET proxy endpoints.</summary>
    public static void ForwardingFailed(
        ILogger logger,
        Exception? exception,
        ForwarderError forwarderError,
        string route,
        string targetEndpoint,
        string targetUrl,
        string? userId,
        string? tenantId,
        string? traceId) =>
        ForwardingFailedCore(logger, exception, route, forwarderError, targetEndpoint, targetUrl, userId, tenantId, traceId, exception?.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "Forwarding failed. Route={Route}, ForwarderError={ForwarderError}, TargetEndpoint={TargetEndpoint}, TargetUrl={TargetUrl}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void ForwardingFailedCore(
        ILogger logger, Exception? exception, string route, ForwarderError forwarderError,
        string targetEndpoint, string targetUrl, string? userId, string? tenantId, string? traceId, string exceptionType);

    #endregion

    #region --- AgentVisibilityService ---

    [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "Failed to get published agent IDs via Redis")]
    public static partial void GetPublishedAgentIdsFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Warning, Message = "Failed to get agent config for {AgentId} via Redis")]
    public static partial void GetAgentConfigFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Warning, Message = "Failed to read ACL entry for {AgentId} via Redis")]
    public static partial void ReadAclEntryFailed(ILogger logger, Exception exception, string agentId);

    #endregion

    #region --- Service Discovery (RedisServiceDiscoveryRouteTable) ---

    [LoggerMessage(EventId = 3010, Level = LogLevel.Debug, Message = "Redis not available, skipping dynamic service discovery")]
    public static partial void RedisNotAvailableForDiscovery(ILogger logger);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Debug, Message = "No healthy engines found in snapshot")]
    public static partial void NoHealthyEnginesInSnapshot(ILogger logger);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Debug, Message = "Session affinity selected engine {EngineId} for conversation {ConversationId}")]
    public static partial void SessionAffinityEngineSelected(ILogger logger, string engineId, string conversationId);

    [LoggerMessage(EventId = 3013, Level = LogLevel.Warning, Message = "Session affinity target engine {EngineId} not in healthy list, falling back to lowest load")]
    public static partial void AffinityEngineNotInHealthyList(ILogger logger, string engineId);

    [LoggerMessage(EventId = 3014, Level = LogLevel.Warning, Message = "No healthy engines found in Redis registry")]
    public static partial void NoHealthyEnginesInRegistry(ILogger logger);

    [LoggerMessage(EventId = 3015, Level = LogLevel.Information, Message = "Selected engine {EngineId} at {Endpoint} with load {Load}")]
    public static partial void EngineSelected(ILogger logger, string engineId, string endpoint, int load);

    [LoggerMessage(EventId = 3016, Level = LogLevel.Warning, Message = "Unexpected error during service discovery")]
    public static partial void DiscoveryUnexpectedError(ILogger logger, Exception exception);

    #endregion

    #region --- Engine Registry Snapshot Cache ---

    [LoggerMessage(EventId = 3017, Level = LogLevel.Warning, Message = "Failed to refresh engine registry snapshot")]
    public static partial void RefreshSnapshotFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3018, Level = LogLevel.Warning, Message = "Failed to refresh engine registry snapshot. RedisFailureMode={RedisFailureMode}")]
    public static partial void RefreshSnapshotUnavailable(ILogger logger, Exception exception, string redisFailureMode);

    [LoggerMessage(EventId = 3019, Level = LogLevel.Warning, Message = "Engine {EngineId} heartbeat is stale ({HeartbeatAge}s), skipping")]
    public static partial void EngineHeartbeatStale(ILogger logger, string engineId, double heartbeatAge);

    [LoggerMessage(EventId = 3020, Level = LogLevel.Warning, Message = "Failed to deserialize engine registry entry for key {Key}")]
    public static partial void EngineEntryDeserializationFailed(ILogger logger, Exception exception, RedisKey key);

    #endregion

    #region --- CompositeRouteTable ---

    [LoggerMessage(EventId = 3021, Level = LogLevel.Debug, Message = "Dynamic discovery returned endpoint: {Endpoint}")]
    public static partial void DynamicDiscoveryReturnedEndpoint(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3022, Level = LogLevel.Debug, Message = "Falling back to static endpoint: {Endpoint}")]
    public static partial void FallbackToStaticEndpoint(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3023, Level = LogLevel.Warning, Message = "No endpoint found for intent: {Intent}")]
    public static partial void NoEndpointForIntent(ILogger logger, string intent);

    #endregion

    #region --- JWT User Context Middleware ---

    [LoggerMessage(EventId = 3024, Level = LogLevel.Debug, Message = "Authenticated user context created. UserId={UserId}, TenantId={TenantId}, RoleCount={RoleCount}, GroupCount={GroupCount}, AudienceCount={AudienceCount}")]
    public static partial void AuthenticatedUserContextCreated(ILogger logger, string userId, string? tenantId, int roleCount, int groupCount, int audienceCount);

    [LoggerMessage(EventId = 3025, Level = LogLevel.Debug, Message = "Anonymous user context created. Path={Path}, TraceId={TraceId}")]
    public static partial void AnonymousUserContextCreated(ILogger logger, PathString path, string traceId);

    #endregion

    #region --- Rate Limiter ---

    [LoggerMessage(EventId = 3026, Level = LogLevel.Warning, Message = "Redis rate limiting failed. ClientId={ClientId}, FailureMode={FailureMode}")]
    public static partial void RateLimitRedisFailed(ILogger logger, Exception exception, string clientId, string failureMode);

    [LoggerMessage(EventId = 3027, Level = LogLevel.Debug, Message = "Redis rate limiting is not configured. ClientId={ClientId}, FailureMode={FailureMode}")]
    public static partial void RateLimitRedisNotConfigured(ILogger logger, string clientId, string failureMode);

    #endregion

    #region --- Health Check ---

    [LoggerMessage(EventId = 3028, Level = LogLevel.Warning, Message = "Redis ping failed during readiness check")]
    public static partial void RedisPingFailedDuringReadinessCheck(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3029, Level = LogLevel.Warning, Message = "No eligible Engine supports the route. Intent={Intent}")]
    public static partial void NoEligibleEngines(ILogger logger, string intent);

    [LoggerMessage(EventId = 3030, Level = LogLevel.Warning, Message = "Failed to remove {Count} stale members from the Engine registry index")]
    public static partial void RegistryIndexCleanupFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(EventId = 3031, Level = LogLevel.Warning, Message = "Downstream Engine readiness probe failed. Endpoint={Endpoint}")]
    public static partial void DownstreamProbeFailed(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 3032, Level = LogLevel.Warning, Message = "Downstream Engine is not ready. Endpoint={Endpoint}, StatusCode={StatusCode}")]
    public static partial void DownstreamNotReady(ILogger logger, string endpoint, int statusCode);

    [LoggerMessage(EventId = 3033, Level = LogLevel.Warning, Message = "Downstream endpoint quarantined after forwarding failure. Endpoint={Endpoint}")]
    public static partial void DownstreamQuarantined(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3034, Level = LogLevel.Information, Message = "Readiness probe used fallback downstream. FailedEndpoint={FailedEndpoint}, FallbackEndpoint={FallbackEndpoint}")]
    public static partial void ReadinessFallbackSelected(ILogger logger, string failedEndpoint, string fallbackEndpoint);

    #endregion

}
