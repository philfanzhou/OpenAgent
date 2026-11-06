using Microsoft.Extensions.Configuration;

namespace OpenAgent.Engine.Hosting;

/// <summary>
/// Per-signal OTLP export configuration.
/// </summary>
/// <remarks>
/// A single shared endpoint is not enough for the audit plane. Traces must go to
/// the masking gateway (which decides what is sensitive and stores the vault),
/// while logs and metrics usually belong to a different collector — or to none
/// at all. Sending everything to one endpoint either leaks unmasked logs to the
/// audit store or floods it with operational noise.
///
/// Resolution per signal, highest priority first:
/// <list type="number">
///   <item><c>OpenTelemetry:{Signal}Endpoint</c> / <c>OTEL_EXPORTER_OTLP_{SIGNAL}_ENDPOINT</c></item>
///   <item><c>OpenTelemetry:OtlpEndpoint</c> / <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (shared fallback)</item>
///   <item>disabled</item>
/// </list>
/// Headers follow the same shape, so one signal can carry a collector token
/// without sending that token to the others.
/// </remarks>
public sealed class OtlpSettings
{
    private OtlpSettings(
        Uri? traces,
        Uri? logs,
        Uri? metrics,
        IReadOnlyDictionary<string, string> traceHeaders,
        IReadOnlyDictionary<string, string> logHeaders,
        IReadOnlyDictionary<string, string> metricHeaders,
        string? protocol)
    {
        TracesEndpoint = traces;
        LogsEndpoint = logs;
        MetricsEndpoint = metrics;
        TraceHeaders = traceHeaders;
        LogHeaders = logHeaders;
        MetricHeaders = metricHeaders;
        Protocol = protocol;
    }

    public Uri? TracesEndpoint { get; }

    public Uri? LogsEndpoint { get; }

    public Uri? MetricsEndpoint { get; }

    public IReadOnlyDictionary<string, string> TraceHeaders { get; }

    public IReadOnlyDictionary<string, string> LogHeaders { get; }

    public IReadOnlyDictionary<string, string> MetricHeaders { get; }

    /// <summary>The OTLP wire protocol, or <see langword="null"/> to use the SDK default.</summary>
    public string? Protocol { get; }

    public static OtlpSettings FromConfiguration(IConfiguration configuration) =>
        new(
            EndpointFor(configuration, OtlpSignal.Traces),
            EndpointFor(configuration, OtlpSignal.Logs),
            EndpointFor(configuration, OtlpSignal.Metrics),
            HeadersFor(configuration, OtlpSignal.Traces),
            HeadersFor(configuration, OtlpSignal.Logs),
            HeadersFor(configuration, OtlpSignal.Metrics),
            NullIfBlank(configuration["OpenTelemetry:Protocol"]
                ?? configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]));

    /// <summary>Resolves the endpoint for one signal, or <see langword="null"/> when disabled.</summary>
    public static Uri? EndpointFor(IConfiguration configuration, OtlpSignal signal)
    {
        string signalName = signal.ToString();
        string? configured =
            NullIfBlank(configuration[$"OpenTelemetry:{signalName}Endpoint"])
            ?? NullIfBlank(Environment.GetEnvironmentVariable($"OTEL_EXPORTER_OTLP_{signalName.ToUpperInvariant()}_ENDPOINT"))
            // Shared fallback last: a trailing slash or empty value in config must not
            // shadow the environment variable the deployment actually set.
            ?? NullIfBlank(configuration["OpenTelemetry:OtlpEndpoint"])
            ?? NullIfBlank(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

        return ParseEndpoint(configured, $"OpenTelemetry:{signalName}Endpoint");
    }

    /// <summary>Parses the <c>key=value,key=value</c> header list for one signal.</summary>
    public static IReadOnlyDictionary<string, string> HeadersFor(IConfiguration configuration, OtlpSignal signal)
    {
        string signalName = signal.ToString();
        string? configured =
            NullIfBlank(configuration[$"OpenTelemetry:{signalName}Headers"])
            ?? NullIfBlank(Environment.GetEnvironmentVariable($"OTEL_EXPORTER_OTLP_{signalName.ToUpperInvariant()}_HEADERS"))
            ?? NullIfBlank(configuration["OpenTelemetry:OtlpHeaders"])
            ?? NullIfBlank(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"));

        return ParseHeaders(configured);
    }

    /// <summary>Headers for one signal, already resolved.</summary>
    public IReadOnlyDictionary<string, string> HeadersFor(OtlpSignal signal) => signal switch
    {
        OtlpSignal.Traces => TraceHeaders,
        OtlpSignal.Logs => LogHeaders,
        OtlpSignal.Metrics => MetricHeaders,
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };

    /// <summary>
    /// The signal path appended when the endpoint carries none.
    /// </summary>
    public string PathFor(OtlpSignal signal) => signal switch
    {
        OtlpSignal.Traces => "v1/traces",
        OtlpSignal.Logs => "v1/logs",
        OtlpSignal.Metrics => "v1/metrics",
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };

    /// <summary>
    /// Validates one endpoint string. Throws for a malformed value so a typo fails
    /// at startup instead of silently dropping every span.
    /// </summary>
    internal static Uri? ParseEndpoint(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{settingName} must be an absolute HTTP(S) URI. Value: '{value}'.");
        }

        return endpoint;
    }

    internal static IReadOnlyDictionary<string, string> ParseHeaders(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmptyHeaders;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                // A header without '=' cannot be sent; failing loudly here beats
                // discovering at runtime that the collector rejects unauthenticated batches.
                throw new InvalidOperationException(
                    $"OTLP header entry '{pair}' is not in 'key=value' form.");
            }

            headers[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
        }

        return headers;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The three OTLP signals this platform exports.</summary>
public enum OtlpSignal
{
    Traces,
    Logs,
    Metrics
}