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

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Agent access denied. Action={Action}, UserId={UserId}, TenantId={TenantId}, AgentId={AgentId}, ConversationId={ConversationId}, TraceId={TraceId}")]
    public static partial void AgentAccessDenied(ILogger logger, string? action, string userId, string? tenantId, string agentId, string? conversationId, string? traceId);

    // Idempotency cache
    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "Idempotency cache hit. Action={Action}, IdempotencyKey={IdempotencyKey}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}")]
    public static partial void IdempotencyCacheHit(ILogger logger, string? action, string idempotencyKey, string userId, string? tenantId, string? traceId);

    public static void IdempotencyCacheCheckFailed(ILogger logger, Exception exception, string? action, string idempotencyKey, string? traceId) =>
        IdempotencyCacheCheckFailedCore(logger, exception, action, idempotencyKey, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 3011, Level = LogLevel.Warning, Message = "Idempotency cache check failed, bypassing idempotency. Action={Action}, IdempotencyKey={IdempotencyKey}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void IdempotencyCacheCheckFailedCore(ILogger logger, Exception exception, string? action, string idempotencyKey, string? traceId, string exceptionType);

    // Body reading & parsing
    public static void BodyReadFailed(ILogger logger, Exception exception, string? action, string method, PathString path, string? traceId) =>
        BodyReadFailedCore(logger, exception, action, method, path, traceId, exception.GetType().FullName ?? "unknown");

    [LoggerMessage(EventId = 3020, Level = LogLevel.Warning, Message = "Failed to read request body. Action={Action}, Method={Method}, Path={Path}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    private static partial void BodyReadFailedCore(ILogger logger, Exception exception, string? action, string method, PathString path, string? traceId, string exceptionType);

    [LoggerMessage(EventId = 3021, Level = LogLevel.Debug, Message = "Request body is not valid JSON for metadata extraction. Action={Action}, Method={Method}, Path={Path}, TraceId={TraceId}, BodyLength={BodyLength}")]
    public static partial void BodyNotValidJson(ILogger logger, Exception exception, string? action, string method, PathString path, string? traceId, int bodyLength);

    [LoggerMessage(EventId = 3022, Level = LogLevel.Debug, Message = "Failed to extract agent ID from request body JSON. Action={Action}, TraceId={TraceId}, BodyLength={BodyLength}")]
    public static partial void AgentIdExtractionFailed(ILogger logger, Exception exception, string? action, string? traceId, int bodyLength);

    // Request lifecycle
    [LoggerMessage(EventId = 3030, Level = LogLevel.Information, Message = "Request accepted. Action={Action}, UserId={UserId}, TenantId={TenantId}, ConversationId={ConversationId}, QueryLength={QueryLength}, TraceId={TraceId}")]
    public static partial void RequestAccepted(ILogger logger, string? action, string userId, string? tenantId, string? conversationId, int queryLength, string? traceId);

    [LoggerMessage(EventId = 3031, Level = LogLevel.Information, Message = "Semantic cache hit. Action={Action}, UserId={UserId}, TenantId={TenantId}, ConversationId={ConversationId}, TraceId={TraceId}")]
    public static partial void SemanticCacheHit(ILogger logger, string? action, string userId, string? tenantId, string? conversationId, string? traceId);

    [LoggerMessage(EventId = 3032, Level = LogLevel.Information, Message = "Intent recognized. Action={Action}, Intent={Intent}, UserId={UserId}, TenantId={TenantId}, ConversationId={ConversationId}, QueryLength={QueryLength}, TraceId={TraceId}")]
    public static partial void IntentRecognized(ILogger logger, string? action, string intent, string userId, string? tenantId, string? conversationId, int queryLength, string? traceId);

    [LoggerMessage(EventId = 3033, Level = LogLevel.Warning, Message = "Could not determine target service. Action={Action}, Intent={Intent}, UserId={UserId}, TenantId={TenantId}, AgentId={AgentId}, ConversationId={ConversationId}, TraceId={TraceId}")]
    public static partial void TargetServiceNotFound(ILogger logger, string? action, string intent, string userId, string? tenantId, string? agentId, string? conversationId, string? traceId);

    [LoggerMessage(EventId = 3034, Level = LogLevel.Warning, Message = "Intent recognition skipped because no routable agents are available")]
    public static partial void IntentRecognitionNoCandidates(ILogger logger);

    [LoggerMessage(EventId = 3035, Level = LogLevel.Warning, Message = "Intent recognition dependency returned HTTP {StatusCode}. Dependency={Dependency}")]
    public static partial void IntentRecognitionHttpFailure(ILogger logger, int statusCode, string dependency);

    [LoggerMessage(EventId = 3036, Level = LogLevel.Warning, Message = "Intent recognition agent timed out after {TimeoutMs} ms")]
    public static partial void IntentRecognitionTimedOut(ILogger logger, int timeoutMs);

    [LoggerMessage(EventId = 3037, Level = LogLevel.Warning, Message = "Intent recognition agent request failed")]
    public static partial void IntentRecognitionRequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3038, Level = LogLevel.Warning, Message = "Intent recognition agent returned invalid JSON")]
    public static partial void IntentRecognitionInvalidJson(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3039, Level = LogLevel.Information, Message = "Agent selection completed. AgentId={AgentId}, SelectedByIntentAgent={SelectedByIntentAgent}, Confidence={Confidence}, TraceId={TraceId}")]
    public static partial void AgentSelectionCompleted(ILogger logger, string agentId, bool selectedByIntentAgent, double? confidence, string? traceId);

    [LoggerMessage(EventId = 3046, Level = LogLevel.Warning, Message = "Conversation Agent resolution failed. ConversationId={ConversationId}, TraceId={TraceId}")]
    public static partial void ConversationAgentResolutionFailed(ILogger logger, Exception exception, string conversationId, string? traceId);

    // Forwarding
    [LoggerMessage(EventId = 3040, Level = LogLevel.Information, Message = "Forwarding request. Action={Action}, TargetEndpoint={TargetEndpoint}, Intent={Intent}, AgentId={AgentId}, ConversationId={ConversationId}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}")]
    public static partial void ForwardingStarted(ILogger logger, string? action, string targetEndpoint, string intent, string? agentId, string? conversationId, string userId, string? tenantId, string? traceId);

    [LoggerMessage(EventId = 3041, Level = LogLevel.Debug, Message = "Proxy request prepared. Action={Action}, TargetUrl={TargetUrl}, AgentId={AgentId}, ConversationId={ConversationId}, UserId={UserId}, TenantId={TenantId}, TraceId={TraceId}")]
    public static partial void ProxyRequestPrepared(ILogger logger, string? action, string targetUrl, string? agentId, string? conversationId, string userId, string? tenantId, string? traceId);

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

    [LoggerMessage(EventId = 3050, Level = LogLevel.Information, Message = "AgentVisibilityService initialized. UsingStackExchangeRedis={UsingStackExchangeRedis}")]
    public static partial void VisibilityServiceInitialized(ILogger logger, bool usingStackExchangeRedis);

    [LoggerMessage(EventId = 3051, Level = LogLevel.Debug, Message = "No ACL entry found for agent {AgentId}, defaulting to visible")]
    public static partial void AclEntryNotFound(ILogger logger, string agentId);

    [LoggerMessage(EventId = 3052, Level = LogLevel.Warning, Message = "Failed to get published agent IDs via Redis")]
    public static partial void GetPublishedAgentIdsFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3053, Level = LogLevel.Warning, Message = "Failed to get agent config for {AgentId} via Redis")]
    public static partial void GetAgentConfigFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(EventId = 3054, Level = LogLevel.Warning, Message = "Failed to read ACL entry for {AgentId} via Redis")]
    public static partial void ReadAclEntryFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(EventId = 3055, Level = LogLevel.Debug, Message = "ACL raw JSON for {AgentId}: {Json}")]
    public static partial void AclRawJson(ILogger logger, string agentId, string json);

    [LoggerMessage(EventId = 3056, Level = LogLevel.Debug, Message = "ACL deserialized for {AgentId}: AllowedUserIds=[{UserIds}], AllowedGroups=[{Groups}], AllowedTenantIds=[{TenantIds}], AllowedRoles=[{Roles}]")]
    public static partial void AclDeserialized(ILogger logger, string agentId, string userIds, string groups, string tenantIds, string roles);

    // ACL decision logs — grouped by match type
    [LoggerMessage(EventId = 3060, Level = LogLevel.Debug, Message = "ACL for agent has no restrictions, allowing access for user {UserId}")]
    public static partial void AclNoRestrictions(ILogger logger, string userId);

    [LoggerMessage(EventId = 3061, Level = LogLevel.Debug, Message = "User {UserId} allowed via AllowedUserIds")]
    public static partial void AllowedViaUserIds(ILogger logger, string userId);

    [LoggerMessage(EventId = 3062, Level = LogLevel.Debug, Message = "User {UserId} allowed via AllowedGroups")]
    public static partial void AllowedViaGroups(ILogger logger, string userId);

    [LoggerMessage(EventId = 3063, Level = LogLevel.Debug, Message = "User {UserId} allowed via AllowedTenantIds")]
    public static partial void AllowedViaTenantIds(ILogger logger, string userId);

    [LoggerMessage(EventId = 3064, Level = LogLevel.Debug, Message = "User {UserId} allowed via AllowedRoles")]
    public static partial void AllowedViaRoles(ILogger logger, string userId);

    [LoggerMessage(EventId = 3065, Level = LogLevel.Debug, Message = "User {UserId} denied access; no matching ACL rule")]
    public static partial void AccessDeniedByAcl(ILogger logger, string userId);

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
