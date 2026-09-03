namespace OpenAgent.Runner;

internal static partial class RunnerLog
{
    [LoggerMessage(5300, LogLevel.Information, "Code execution {ExecutionId} started. TraceId={TraceId}")]
    internal static partial void Started(ILogger logger, string executionId, string traceId);

    [LoggerMessage(5301, LogLevel.Information, "Code execution {ExecutionId} finished. ExitCode={ExitCode} ElapsedMs={ElapsedMs}")]
    internal static partial void Completed(ILogger logger, string executionId, int exitCode, long elapsedMs);

    [LoggerMessage(5302, LogLevel.Warning, "Code execution {ExecutionId} interrupted or unavailable. Reason={Reason}")]
    internal static partial void Interrupted(ILogger logger, string executionId, string reason);

    [LoggerMessage(5303, LogLevel.Error, "Runner cleanup failed for {ExecutionId}; the reaper will retry.")]
    internal static partial void CleanupFailed(ILogger logger, string executionId);

    [LoggerMessage(5304, LogLevel.Error, "Code execution {ExecutionId} sandbox {Phase} failed. Detail={Detail}")]
    internal static partial void EnvironmentFailed(ILogger logger, string executionId, string phase, string detail);
}
