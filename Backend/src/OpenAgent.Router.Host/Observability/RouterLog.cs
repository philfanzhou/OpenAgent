using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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

    #region --- JWT User Context Middleware ---

    [LoggerMessage(EventId = 3024, Level = LogLevel.Debug, Message = "Authenticated user context created. UserId={UserId}, TenantId={TenantId}, RoleCount={RoleCount}, GroupCount={GroupCount}, AudienceCount={AudienceCount}")]
    public static partial void AuthenticatedUserContextCreated(ILogger logger, string userId, string? tenantId, int roleCount, int groupCount, int audienceCount);

    [LoggerMessage(EventId = 3025, Level = LogLevel.Debug, Message = "Anonymous user context created. Path={Path}, TraceId={TraceId}")]
    public static partial void AnonymousUserContextCreated(ILogger logger, PathString path, string traceId);

    #endregion

    #region --- Health Check ---

    [LoggerMessage(EventId = 3028, Level = LogLevel.Warning, Message = "Redis ping failed during readiness check")]
    public static partial void RedisPingFailedDuringReadinessCheck(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3033, Level = LogLevel.Warning, Message = "Downstream endpoint quarantined after forwarding failure. Endpoint={Endpoint}")]
    public static partial void DownstreamQuarantined(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3034, Level = LogLevel.Information, Message = "Readiness probe used fallback downstream. FailedEndpoint={FailedEndpoint}, FallbackEndpoint={FallbackEndpoint}")]
    public static partial void ReadinessFallbackSelected(ILogger logger, string failedEndpoint, string fallbackEndpoint);

    #endregion

}
