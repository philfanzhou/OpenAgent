using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace OpenAgent.Hosting;

/// <summary>
/// OTLP export settings, resolved once per service.
///
/// Two things here exist for the audit integration and are worth spelling out:
///
/// <para>
/// <b>Per-signal routing.</b> A single <c>OtlpEndpoint</c> sends traces, logs
/// and metrics to the same target. That is fine for a Collector, but the
/// audit plane must only ever receive <i>traces</i> (logs carry the
/// unredacted request text the masking pipeline never sees). <c>TracesEndpoint</c>,
/// <c>LogsEndpoint</c> and <c>MetricsEndpoint</c> override the shared endpoint
/// per signal, so <c>TracesEndpoint</c> can point at the masking gateway while
/// logs and metrics still go to the normal Collector.
/// </para>
///
/// <para>
/// <b>Exporter headers.</b> The gateway authenticates callers with a bearer
/// token. The SDK reads <c>OTEL_EXPORTER_OTLP_HEADERS</c> only when it creates
/// the default options itself; this class binds the same variable explicitly
/// (plus <c>OpenTelemetry:Headers</c> and the per-signal
/// <c>OTEL_EXPORTER_OTLP_TRACES_HEADERS</c> / <c>_LOGS_HEADERS</c> /
/// <c>_METRICS_HEADERS</c>) so configuring the endpoint in code no longer
/// silently drops authentication.
/// </para>
/// </summary>
public sealed class OtlpSettings
{
    private const string SectionName = "OpenTelemetry";

    private OtlpSettings(
        string? sharedEndpoint,
        string? tracesEndpoint,
        string? logsEndpoint,
        string? metricsEndpoint,
        string sharedHeaders,
        string tracesHeaders,
        string logsHeaders,
        string metricsHeaders,
        string? protocol)
    {
        SharedEndpoint = sharedEndpoint;
        TracesEndpoint = tracesEndpoint;
        LogsEndpoint = logsEndpoint;
        MetricsEndpoint = metricsEndpoint;
        SharedHeaders = sharedHeaders;
        TracesHeaders = tracesHeaders;
        LogsHeaders = logsHeaders;
        MetricsHeaders = metricsHeaders;
        Protocol = protocol;
    }

    /// <summary>Shared OTLP endpoint (config value, else the standard env var).</summary>
    public string? SharedEndpoint { get; }

    public string? TracesEndpoint { get; }
    public string? LogsEndpoint { get; }
    public string? MetricsEndpoint { get; }

    /// <summary>Shared exporter headers, e.g. <c>Authorization=Bearer token</c>.</summary>
    public string SharedHeaders { get; }
    public string TracesHeaders { get; }
    public string LogsHeaders { get; }
    public string MetricsHeaders { get; }

    /// <summary>Raw protocol value; <c>null</c> leaves the SDK default (gRPC).</summary>
    public string? Protocol { get; }

    public bool HasAnyEndpoint =>
        !string.IsNullOrWhiteSpace(SharedEndpoint)
        || !string.IsNullOrWhiteSpace(TracesEndpoint)
        || !string.IsNullOrWhiteSpace(LogsEndpoint)
        || !string.IsNullOrWhiteSpace(MetricsEndpoint);

    /// <summary>
    /// Resolves the endpoint for a signal. A per-signal endpoint wins; an empty
    /// per-signal value falls back to the shared endpoint, so a deployment only
    /// needs to override the signals it routes elsewhere.
    /// </summary>
    public string? EndpointFor(OtlpSignal signal)
    {
        string? specific = signal switch
        {
            OtlpSignal.Traces => TracesEndpoint,
            OtlpSignal.Logs => LogsEndpoint,
            OtlpSignal.Metrics => MetricsEndpoint,
            _ => null
        };

        return string.IsNullOrWhiteSpace(specific) ? SharedEndpoint : specific;
    }

    /// <summary>
    /// Headers for a signal: per-signal headers when given, otherwise the shared
    /// set. Headers follow the OTLP environment convention
    /// (<c>key1=value1,key2=value2</c>).
    /// </summary>
    public string? HeadersFor(OtlpSignal signal)
    {
        string specific = signal switch
        {
            OtlpSignal.Traces => TracesHeaders,
            OtlpSignal.Logs => LogsHeaders,
            OtlpSignal.Metrics => MetricsHeaders,
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(specific) ? SharedHeaders : specific;
    }

    public static OtlpSettings FromConfiguration(IConfiguration configuration)
    {
        static string? Value(IConfiguration config, string key, string envVar)
        {
            string? configured = config[$"{SectionName}:{key}"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            string? fromEnv = Environment.GetEnvironmentVariable(envVar);
            return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
        }

        static string Headers(IConfiguration config, string key, string envVar)
        {
            string? configured = config[$"{SectionName}:{key}"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            return Environment.GetEnvironmentVariable(envVar)?.Trim() ?? string.Empty;
        }

        return new OtlpSettings(
            sharedEndpoint: Value(configuration, "OtlpEndpoint", "OTEL_EXPORTER_OTLP_ENDPOINT"),
            tracesEndpoint: Value(configuration, "TracesEndpoint", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"),
            logsEndpoint: Value(configuration, "LogsEndpoint", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"),
            metricsEndpoint: Value(configuration, "MetricsEndpoint", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"),
            sharedHeaders: Headers(configuration, "Headers", "OTEL_EXPORTER_OTLP_HEADERS"),
            tracesHeaders: Headers(configuration, "TracesHeaders", "OTEL_EXPORTER_OTLP_TRACES_HEADERS"),
            logsHeaders: Headers(configuration, "LogsHeaders", "OTEL_EXPORTER_OTLP_LOGS_HEADERS"),
            metricsHeaders: Headers(configuration, "MetricsHeaders", "OTEL_EXPORTER_OTLP_METRICS_HEADERS"),
            protocol: Value(configuration, "Protocol", "OTEL_EXPORTER_OTLP_PROTOCOL"));
    }

    /// <summary>
    /// Validates an endpoint string as an absolute HTTP(S) URI.
    /// A non-URI must fail startup: exporting to a silently invalid address
    /// loses telemetry without any error surfacing anywhere.
    /// </summary>
    public static Uri ParseEndpoint(string value, OtlpSignal signal)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"OpenTelemetry {signal} endpoint must be an absolute HTTP(S) URI. Value: '{value}'. "
                + "Set OpenTelemetry:OtlpEndpoint (or the matching OTEL_EXPORTER_OTLP_ENDPOINT variable).");
        }

        return endpoint;
    }

    /// <summary>
    /// Parses the OTLP protocol name (<c>grpc</c>, <c>http/protobuf</c>,
    /// <c>http/json</c>). Returns <c>null</c> when unset (SDK default: gRPC) and
    /// throws on an unrecognised value instead of silently exporting over the
    /// wrong transport.
    /// </summary>
    public OpenTelemetry.Exporter.OtlpExportProtocol? ResolveProtocol()
    {
        if (string.IsNullOrWhiteSpace(Protocol))
        {
            return null;
        }

        return Protocol.Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            "grpc" => OpenTelemetry.Exporter.OtlpExportProtocol.Grpc,
            "http/protobuf" => OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf,
            "http/json" => OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf,
            _ => throw new InvalidOperationException(
                $"Unsupported OpenTelemetry protocol '{Protocol}'. Expected grpc, http/protobuf or http/json.")
        };
    }
}

/// <summary>OTLP signals that can be routed independently.</summary>
public enum OtlpSignal
{
    Traces,
    Logs,
    Metrics
}