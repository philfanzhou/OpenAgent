using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Observability;

/// <summary>
/// Centralized logging definitions for the Router project using [LoggerMessage] source generators.
/// EventId range: 3000–3199.
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
    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "Idempotency cache hit. Action={Action}, IdempotencyKey={IdempotencyKey}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}")]
    public static partial void IdempotencyCacheHit(ILogger logger, string? action, string idempotencyKey, string userId, string? tenantId, string? traceId);

    public static void IdempotencyCacheCheckFailed(ILogger logger, Exception exception, string? action, string idempotencyKey, string? traceId) =>
        IdempotencyCacheCheckFailedCore(logger, exception, action, idempotencyKey, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 3011, Level = LogLevel.Warning, Message = "Idempotency cache check failed, bypassing idempotency. Action={Action}, IdempotencyKey={IdempotencyKey}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void IdempotencyCacheCheckFailedCore(ILogger logger, Exception exception, string? action, string idempotencyKey, string? traceId, string exceptionType);

    [LoggerMessage(EventId = 3031, Level = LogLevel.Information, Message = "Semantic cache hit. Action={Action}, UserId={UserId}, TenantId={TenantId}, ConversationId={ConversationId}, TraceId={TraceId}")]
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

    [LoggerMessage(EventId = 3042, Level = LogLevel.Error, Message = "Forwarding failed. Route={Route}, ForwarderError={ForwarderError}, TargetEndpoint={TargetEndpoint}, TargetUrl={TargetUrl}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void ForwardingFailedCore(
        ILogger logger, Exception? exception, string route, ForwarderError forwarderError,
        string targetEndpoint, string targetUrl, string? userId, string? tenantId, string? traceId, string exceptionType);

    #endregion

    #region --- AgentVisibilityService ---

    [LoggerMessage(EventId = 3052, Level = LogLevel.Warning, Message = "Failed to get published agent IDs via Redis")]
    public static partial void GetPublishedAgentIdsFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3053, Level = LogLevel.Warning, Message = "Failed to get agent config for {AgentId} via Redis")]
    public static partial void GetAgentConfigFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(EventId = 3054, Level = LogLevel.Warning, Message = "Failed to read ACL entry for {AgentId} via Redis")]
    public static partial void ReadAclEntryFailed(ILogger logger, Exception exception, string agentId);

    #endregion

    #region --- Service Discovery (RedisServiceDiscoveryRouteTable) ---

    [LoggerMessage(EventId = 3070, Level = LogLevel.Debug, Message = "Redis not available, skipping dynamic service discovery")]
    public static partial void RedisNotAvailableForDiscovery(ILogger logger);

    [LoggerMessage(EventId = 3071, Level = LogLevel.Debug, Message = "No healthy engines found in snapshot")]
    public static partial void NoHealthyEnginesInSnapshot(ILogger logger);

    [LoggerMessage(EventId = 3072, Level = LogLevel.Debug, Message = "Session affinity selected engine {EngineId} for conversation {ConversationId}")]
    public static partial void SessionAffinityEngineSelected(ILogger logger, string engineId, string conversationId);

    [LoggerMessage(EventId = 3073, Level = LogLevel.Warning, Message = "Session affinity target engine {EngineId} not in healthy list, falling back to lowest load")]
    public static partial void AffinityEngineNotInHealthyList(ILogger logger, string engineId);

    [LoggerMessage(EventId = 3074, Level = LogLevel.Warning, Message = "No healthy engines found in Redis registry")]
    public static partial void NoHealthyEnginesInRegistry(ILogger logger);

    [LoggerMessage(EventId = 3075, Level = LogLevel.Information, Message = "Selected engine {EngineId} at {Endpoint} with load {Load}")]
    public static partial void EngineSelected(ILogger logger, string engineId, string endpoint, int load);

    [LoggerMessage(EventId = 3076, Level = LogLevel.Warning, Message = "Unexpected error during service discovery")]
    public static partial void DiscoveryUnexpectedError(ILogger logger, Exception exception);

    #endregion

    #region --- Engine Registry Snapshot Cache ---

    [LoggerMessage(EventId = 3080, Level = LogLevel.Warning, Message = "Failed to refresh engine registry snapshot")]
    public static partial void RefreshSnapshotFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3081, Level = LogLevel.Warning, Message = "Failed to refresh engine registry snapshot; preserving previous snapshot")]
    public static partial void RefreshSnapshotPreservingPrevious(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3082, Level = LogLevel.Warning, Message = "Engine {EngineId} heartbeat is stale ({HeartbeatAge}s), skipping")]
    public static partial void EngineHeartbeatStale(ILogger logger, string engineId, double heartbeatAge);

    [LoggerMessage(EventId = 3083, Level = LogLevel.Warning, Message = "Failed to deserialize engine registry entry for key {Key}")]
    public static partial void EngineEntryDeserializationFailed(ILogger logger, Exception exception, RedisKey key);

    #endregion

    #region --- CompositeRouteTable ---

    [LoggerMessage(EventId = 3090, Level = LogLevel.Debug, Message = "Dynamic discovery returned endpoint: {Endpoint}")]
    public static partial void DynamicDiscoveryReturnedEndpoint(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3091, Level = LogLevel.Debug, Message = "Falling back to static endpoint: {Endpoint}")]
    public static partial void FallbackToStaticEndpoint(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3092, Level = LogLevel.Warning, Message = "No endpoint found for intent: {Intent}")]
    public static partial void NoEndpointForIntent(ILogger logger, string intent);

    #endregion

    #region --- Audit Logging Middleware ---

    public static void AuditBodyParseFailed(ILogger logger, Exception exception, string method, PathString path, string? traceId) =>
        AuditBodyParseFailedCore(logger, exception, method, path, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 3100, Level = LogLevel.Debug, Message = "Audit request body parse failed. Method={Method}, Path={Path}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void AuditBodyParseFailedCore(ILogger logger, Exception exception, string method, PathString path, string? traceId, string exceptionType);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Request completed. TraceId={TraceId}, Method={Method}, Path={Path}, UserId={UserId}, TenantId={TenantId}, Query={Query}, StatusCode={StatusCode}, Outcome={Outcome}, DurationMs={DurationMs}")]
    public static partial void AuditRequestCompleted(ILogger logger, string? traceId, string method, PathString path, string userId, string? tenantId, string? query, int statusCode, string outcome, long durationMs);

    public static void AuditRequestFailed(ILogger logger, Exception exception, string? traceId, string method, PathString path, string userId, string? tenantId, string? query, long durationMs) =>
        AuditRequestFailedCore(logger, exception, traceId, method, path, userId, tenantId, query, durationMs, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 3102, Level = LogLevel.Error, Message = "Request failed. TraceId={TraceId}, Method={Method}, Path={Path}, UserId={UserId}, TenantId={TenantId}, Query={Query}, Outcome=Exception, DurationMs={DurationMs}, ExceptionType={ExceptionType}")]
    private static partial void AuditRequestFailedCore(ILogger logger, Exception exception, string? traceId, string method, PathString path, string userId, string? tenantId, string? query, long durationMs, string exceptionType);

    #endregion

    #region --- JWT User Context Middleware ---

    [LoggerMessage(EventId = 3110, Level = LogLevel.Debug, Message = "Authenticated user context created. UserId={UserId}, TenantId={TenantId}, RoleCount={RoleCount}, GroupCount={GroupCount}, AudienceCount={AudienceCount}")]
    public static partial void AuthenticatedUserContextCreated(ILogger logger, string userId, string? tenantId, int roleCount, int groupCount, int audienceCount);

    [LoggerMessage(EventId = 3111, Level = LogLevel.Debug, Message = "Anonymous user context created. Path={Path}, TraceId={TraceId}")]
    public static partial void AnonymousUserContextCreated(ILogger logger, PathString path, string traceId);

    #endregion

    #region --- Rate Limiter ---

    [LoggerMessage(EventId = 3120, Level = LogLevel.Warning, Message = "Redis connection failed. Bypassing rate limit for {ClientId} (Fail-open)")]
    public static partial void RateLimitConnectionFailed(ILogger logger, Exception exception, string clientId);

    [LoggerMessage(EventId = 3121, Level = LogLevel.Warning, Message = "Unexpected error in rate limiting. Bypassing rate limit for {ClientId} (Fail-open)")]
    public static partial void RateLimitUnexpectedError(ILogger logger, Exception exception, string clientId);

    #endregion

    #region --- Health Check ---

    [LoggerMessage(EventId = 3130, Level = LogLevel.Warning, Message = "Redis ping failed during readiness check")]
    public static partial void RedisPingFailedDuringReadinessCheck(ILogger logger, Exception exception);

    #endregion

    #region --- Internal Service Auth Middleware ---

    [LoggerMessage(EventId = 3140, Level = LogLevel.Information, Message = "Internal service authenticated. Service={Service}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}")]
    public static partial void InternalServiceAuthenticated(ILogger logger, string service, string userId, string? tenantId, string? traceId);

    [LoggerMessage(EventId = 3141, Level = LogLevel.Warning, Message = "Internal service authentication rejected. Service={Service}, Reason={Reason}, TraceId={TraceId}")]
    public static partial void InternalServiceAuthRejected(ILogger logger, string? service, string reason, string? traceId);

    public static void InternalServiceAuthError(ILogger logger, Exception exception, string? traceId) =>
        InternalServiceAuthErrorCore(logger, exception, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 3142, Level = LogLevel.Warning, Message = "Unexpected error in internal service authentication. TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void InternalServiceAuthErrorCore(ILogger logger, Exception exception, string? traceId, string exceptionType);

    #endregion
}
