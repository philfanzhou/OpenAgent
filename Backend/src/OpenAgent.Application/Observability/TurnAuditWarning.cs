using Microsoft.Extensions.Logging;

namespace OpenAgent.Application.Observability;

internal sealed partial class TurnAuditWarning(ILogger logger, bool traceExportConfigured)
{
    private const long WarningIntervalMilliseconds = 60_000;
    private long _nextWarningAt;

    internal void ReportMissingIdentity()
    {
        if (!traceExportConfigured || !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        long now = Environment.TickCount64;
        long next = Volatile.Read(ref _nextWarningAt);
        if (now >= next && Interlocked.CompareExchange(ref _nextWarningAt, now + WarningIntervalMilliseconds, next) == next)
        {
            LogMissingIdentity(logger);
        }
    }

    [LoggerMessage(EventId = 4200, Level = LogLevel.Warning,
        Message = "OTLP trace export is configured but the conversation turn audit identity could not be attached to a span.")]
    private static partial void LogMissingIdentity(ILogger logger);
}
