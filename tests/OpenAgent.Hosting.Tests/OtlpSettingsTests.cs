using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using Xunit;

namespace OpenAgent.Hosting.Tests;

/// <summary>
/// OTLP settings that the audit integration depends on: per-signal routing
/// (traces to the masking gateway, logs/metrics elsewhere), exporter headers
/// (bearer token), and protocol selection.
/// </summary>
[Collection("OtelEnvironment")]
public class OtlpSettingsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var data = new Dictionary<string, string?>();
        foreach ((string key, string? value) in values)
        {
            data[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void EndpointFor_FallsBackToSharedEndpoint()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(
            Config(("OpenTelemetry:OtlpEndpoint", "http://collector:4318")));

        Assert.Equal("http://collector:4318", settings.EndpointFor(OtlpSignal.Traces));
        Assert.Equal("http://collector:4318", settings.EndpointFor(OtlpSignal.Logs));
        Assert.Equal("http://collector:4318", settings.EndpointFor(OtlpSignal.Metrics));
    }

    [Fact]
    public void EndpointFor_TracesOverrideWins_OtherSignalsKeepShared()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Config(
            ("OpenTelemetry:OtlpEndpoint", "http://collector:4318"),
            ("OpenTelemetry:TracesEndpoint", "http://masking-gateway:8102")));

        Assert.Equal("http://masking-gateway:8102", settings.EndpointFor(OtlpSignal.Traces));
        Assert.Equal("http://collector:4318", settings.EndpointFor(OtlpSignal.Logs));
        Assert.Equal("http://collector:4318", settings.EndpointFor(OtlpSignal.Metrics));
    }

    [Fact]
    public void EndpointFor_SpecificSignalOnly_LeavesOtherSignalsUnset()
    {
        // The audit-only deployment: no shared endpoint at all.
        OtlpSettings settings = OtlpSettings.FromConfiguration(
            Config(("OpenTelemetry:TracesEndpoint", "http://masking-gateway:8102")));

        Assert.Equal("http://masking-gateway:8102", settings.EndpointFor(OtlpSignal.Traces));
        Assert.Null(settings.EndpointFor(OtlpSignal.Logs));
        Assert.Null(settings.EndpointFor(OtlpSignal.Metrics));
    }

    [Fact]
    public void EndpointFor_BlankSpecificFallsBackToShared()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Config(
            ("OpenTelemetry:OtlpEndpoint", "http://collector:4318"),
            ("OpenTelemetry:TracesEndpoint", "")));

        Assert.Equal("http://collector:4318", settings.EndpointFor(OtlpSignal.Traces));
    }

    [Fact]
    public void HeadersFor_PerSignalOverride_AndSharedFallback()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Config(
            ("OpenTelemetry:Headers", "x-shared=1"),
            ("OpenTelemetry:TracesHeaders", "Authorization=Bearer audit-token")));

        Assert.Equal("Authorization=Bearer audit-token", settings.HeadersFor(OtlpSignal.Traces));
        Assert.Equal("x-shared=1", settings.HeadersFor(OtlpSignal.Logs));
        Assert.Equal("x-shared=1", settings.HeadersFor(OtlpSignal.Metrics));
    }

    [Fact]
    public void Headers_ReadStandardEnvironmentVariables()
    {
        try
        {
            Environment.SetEnvironmentVariable(
                "OTEL_EXPORTER_OTLP_TRACES_HEADERS", "Authorization=Bearer env-token");
            Environment.SetEnvironmentVariable(
                "OTEL_EXPORTER_OTLP_HEADERS", "x-shared=env");

            OtlpSettings settings = OtlpSettings.FromConfiguration(new ConfigurationBuilder().Build());

            Assert.Equal("Authorization=Bearer env-token", settings.HeadersFor(OtlpSignal.Traces));
            Assert.Equal("x-shared=env", settings.HeadersFor(OtlpSignal.Logs));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_HEADERS", null);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", null);
        }
    }

    [Fact]
    public void Headers_ConfigurationWinsOverEnvironment()
    {
        try
        {
            Environment.SetEnvironmentVariable(
                "OTEL_EXPORTER_OTLP_TRACES_HEADERS", "Authorization=Bearer from-env");

            OtlpSettings settings = OtlpSettings.FromConfiguration(
                Config(("OpenTelemetry:TracesHeaders", "Authorization=Bearer from-config")));

            Assert.Equal("Authorization=Bearer from-config", settings.HeadersFor(OtlpSignal.Traces));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_HEADERS", null);
        }
    }

    [Theory]
    [InlineData("grpc", OtlpExportProtocol.Grpc)]
    [InlineData("GRPC", OtlpExportProtocol.Grpc)]
    [InlineData("http/protobuf", OtlpExportProtocol.HttpProtobuf)]
    [InlineData("HTTP/Protobuf", OtlpExportProtocol.HttpProtobuf)]
    [InlineData("http/json", OtlpExportProtocol.HttpProtobuf)]
    public void ResolveProtocol_AcceptsKnownValues(string value, OtlpExportProtocol expected)
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Config(("OpenTelemetry:Protocol", value)));

        Assert.Equal(expected, settings.ResolveProtocol());
    }

    [Fact]
    public void ResolveProtocol_UnsetMeansSdkDefault()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Null(settings.ResolveProtocol());
    }

    [Fact]
    public void ResolveProtocol_UnknownValueFailsFast()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Config(("OpenTelemetry:Protocol", "thrift")));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => settings.ResolveProtocol());
        Assert.Contains("thrift", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("ftp://collector.example.com")]
    [InlineData("/relative/path")]
    public void ParseEndpoint_RejectsInvalidValues(string value)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => OtlpSettings.ParseEndpoint(value, OtlpSignal.Traces));

        Assert.Contains("absolute HTTP(S) URI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEndpoint_ErrorMessageNamesTheSignal()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => OtlpSettings.ParseEndpoint("nope", OtlpSignal.Traces));

        Assert.Contains("Traces", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HasAnyEndpoint_ReflectsConfiguredSignals()
    {
        Assert.False(OtlpSettings.FromConfiguration(new ConfigurationBuilder().Build()).HasAnyEndpoint);
        Assert.True(OtlpSettings.FromConfiguration(
            Config(("OpenTelemetry:TracesEndpoint", "http://gateway:8102"))).HasAnyEndpoint);
    }

    [Fact]
    public void ApplyOtlpExporter_RegistersExporter_WithHeadersAndSignalPath()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Config(
            ("OpenTelemetry:TracesEndpoint", "http://masking-gateway:8102"),
            ("OpenTelemetry:TracesHeaders", "Authorization=Bearer t"),
            ("OpenTelemetry:Protocol", "http/protobuf")));

        var captured = new List<OtlpExporterOptions>();
        var builder = new List<object>();

        // Exercise the same option-building path the real registrations use.
        OtlpExporterOptions? options = InvokeTryCreateExporterOptions(settings, OtlpSignal.Traces, "v1/traces");

        Assert.NotNull(options);
        captured.Add(options!);
        Assert.Equal(new Uri("http://masking-gateway:8102/v1/traces"), captured[0].Endpoint);
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, captured[0].Protocol);
        Assert.Equal("Authorization=Bearer t", captured[0].Headers);
    }

    [Fact]
    public void ApplyOtlpExporter_SkipsSignalWithNoEndpoint()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(
            Config(("OpenTelemetry:TracesEndpoint", "http://masking-gateway:8102")));

        Assert.NotNull(InvokeTryCreateExporterOptions(settings, OtlpSignal.Traces, "v1/traces"));
        Assert.Null(InvokeTryCreateExporterOptions(settings, OtlpSignal.Logs, "v1/logs"));
    }

    private static OtlpExporterOptions? InvokeTryCreateExporterOptions(
        OtlpSettings settings, OtlpSignal signal, string signalPath)
    {
        var method = typeof(ServiceCollectionExtensions).GetMethod(
            "TryCreateExporterOptions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (OtlpExporterOptions?)method!.Invoke(null, [settings, signal, signalPath]);
    }
}