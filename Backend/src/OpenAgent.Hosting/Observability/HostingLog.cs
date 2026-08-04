using Microsoft.Extensions.Logging;

namespace OpenAgent.Hosting.Observability;

internal static partial class HostingLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Warning,
        Message = "JWT authentication failed. Method={Method}, Path={Path}, TraceId={TraceId}, ExceptionType={ExceptionType}")]
    public static partial void JwtAuthenticationFailed(
        ILogger logger,
        Exception exception,
        string method,
        string path,
        string traceId,
        string? exceptionType);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug,
        Message = "JWT token validated. Subject={Subject}, Method={Method}, Path={Path}, TraceId={TraceId}")]
    public static partial void JwtTokenValidated(
        ILogger logger,
        string subject,
        string method,
        string path,
        string traceId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Debug,
        Message = "JWT message received. Method={Method}, Path={Path}, TraceId={TraceId}, HasToken={HasToken}, HasAuthorizationHeader={HasAuthorizationHeader}")]
    public static partial void JwtMessageReceived(
        ILogger logger,
        string method,
        string path,
        string traceId,
        bool hasToken,
        bool hasAuthorizationHeader);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "JWT challenge issued. Method={Method}, Path={Path}, TraceId={TraceId}, Error={Error}, ErrorDescription={ErrorDescription}")]
    public static partial void JwtChallengeIssued(
        ILogger logger,
        string method,
        string path,
        string traceId,
        string? error,
        string? errorDescription);

}
