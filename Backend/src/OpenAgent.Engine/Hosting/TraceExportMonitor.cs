using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;

namespace OpenAgent.Engine.Hosting;

internal sealed partial class TraceExportMonitor(OtlpExporterOptions options, ILogger<TraceExportMonitor> logger)
    : OtlpTraceExporter(options)
{
    internal const string MeterName = "OpenAgent.TraceExport";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("openagent.trace.export.failures");
    private long _nextWarningAt;

    public override ExportResult Export(in Batch<Activity> batch)
    {
        ExportResult result = base.Export(batch);
        if (result == ExportResult.Failure)
        {
            Failures.Add(1);
            long now = Environment.TickCount64;
            long previous = Volatile.Read(ref _nextWarningAt);
            if (now >= previous && Interlocked.CompareExchange(ref _nextWarningAt, now + 60_000, previous) == previous)
                LogFailure(logger);
        }
        return result;
    }

    [LoggerMessage(EventId = 4080, Level = LogLevel.Warning, Message = "OTLP trace batch export failed.")]
    private static partial void LogFailure(ILogger logger);
}
