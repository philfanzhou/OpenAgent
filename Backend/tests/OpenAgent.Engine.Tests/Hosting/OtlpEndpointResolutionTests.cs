using Microsoft.Extensions.Configuration;
using OpenAgent.Engine.Hosting;
using OpenTelemetry.Exporter;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

// Shares OTEL_* environment variables with the other OpenTelemetry tests; serialize them.
[Collection("OtelEnvironment")]
public class OtlpEndpointResolutionTests
{
    private static IConfiguration Build(Action<Dictionary<string, string?>>? configure = null)
    {
        var values = new Dictionary<string, string?>();
        configure?.Invoke(values);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void EndpointFor_ReturnsNull_WhenNothingIsConfigured()
    {
        Assert.Null(OtlpSettings.EndpointFor(Build(), OtlpSignal.Traces));
    }

    [Fact]
    public void EndpointFor_FallsBackToStandardEnvVar_WhenConfigValueIsBlank()
    {
        // Compose passes the value as an empty string when the deployment variable
        // is unset; the blank value must not shadow the OTEL_ env var.
        IConfiguration configuration = Build(v =>
            v["OpenTelemetry:OtlpEndpoint"] = "");

        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4318");

            Assert.Equal(
                new Uri("http://collector:4318"),
                OtlpSettings.EndpointFor(configuration, OtlpSignal.Traces));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        }
    }

    [Fact]
    public void EndpointFor_ConfigValueTakesPrecedenceOverEnvVar()
    {
        IConfiguration configuration = Build(v =>
            v["OpenTelemetry:OtlpEndpoint"] = "http://config-collector:4317");

        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://env-collector:4318");

            Assert.Equal(
                new Uri("http://config-collector:4317"),
                OtlpSettings.EndpointFor(configuration, OtlpSignal.Traces));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        }
    }

    [Fact]
    public void EndpointFor_PerSignalConfigWinsOverSharedEndpoint()
    {
        IConfiguration configuration = Build(v =>
        {
            v["OpenTelemetry:OtlpEndpoint"] = "http://shared:4318";
            v["OpenTelemetry:TracesEndpoint"] = "http://masking-gateway:8102";
        });

        // Traces go to the audit gateway while the rest stays on the shared collector.
        Assert.Equal(
            new Uri("http://masking-gateway:8102"),
            OtlpSettings.EndpointFor(configuration, OtlpSignal.Traces));
        Assert.Equal(
            new Uri("http://shared:4318"),
            OtlpSettings.EndpointFor(configuration, OtlpSignal.Logs));
        Assert.Equal(
            new Uri("http://shared:4318"),
            OtlpSettings.EndpointFor(configuration, OtlpSignal.Metrics));
    }

    [Fact]
    public void EndpointFor_SignalsAreIndependent()
    {
        IConfiguration configuration = Build(v =>
            v["OpenTelemetry:TracesEndpoint"] = "http://masking-gateway:8102");

        Assert.NotNull(OtlpSettings.EndpointFor(configuration, OtlpSignal.Traces));
        // Audit data is traces; logs and metrics stay off unless configured.
        Assert.Null(OtlpSettings.EndpointFor(configuration, OtlpSignal.Logs));
        Assert.Null(OtlpSettings.EndpointFor(configuration, OtlpSignal.Metrics));
    }

    [Theory]
    [InlineData("not-an-absolute-uri")]
    [InlineData("ftp://collector.example.com")]
    public void EndpointFor_WithInvalidEnvVarFallback_Throws(string endpoint)
    {
        IConfiguration configuration = Build(v =>
            v["OpenTelemetry:OtlpEndpoint"] = " ");

        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => OtlpSettings.EndpointFor(configuration, OtlpSignal.Traces));

            Assert.Contains("absolute HTTP(S) URI", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        }
    }

    [Fact]
    public void HeadersFor_ParsesKeyValuePairs()
    {
        IConfiguration configuration = Build(v =>
            v["OpenTelemetry:TracesHeaders"] = "Authorization=Bearer abc123,X-Scope=audit");

        IReadOnlyDictionary<string, string> headers = OtlpSettings.HeadersFor(configuration, OtlpSignal.Traces);

        Assert.Equal("Bearer abc123", headers["Authorization"]);
        Assert.Equal("audit", headers["X-Scope"]);
    }

    [Fact]
    public void HeadersFor_AreIndependentPerSignal()
    {
        IConfiguration configuration = Build(v =>
            v["OpenTelemetry:TracesHeaders"] = "Authorization=Bearer secret");

        Assert.Equal("Bearer secret", OtlpSettings.HeadersFor(configuration, OtlpSignal.Traces)["Authorization"]);
        // The collector token must not leak to the logs signal.
        Assert.Empty(OtlpSettings.HeadersFor(configuration, OtlpSignal.Logs));
    }

    [Fact]
    public void HeadersFor_RejectsMalformedEntry()
    {
        IConfiguration configuration = Build(v =>
            v["OpenTelemetry:TracesHeaders"] = "NoEqualsSign");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => OtlpSettings.HeadersFor(configuration, OtlpSignal.Traces));

        Assert.Contains("key=value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyOtlpExporter_WithHttpProtobufAndPathlessEndpoint_AppendsSignalPath()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Build());
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };

        ServiceCollectionExtensions.ApplyOtlpExporter(
            exporter, settings, OtlpSignal.Logs, new Uri("http://collector:4318"));

        Assert.Equal(new Uri("http://collector:4318/v1/logs"), exporter.Endpoint);
    }

    [Fact]
    public void ApplyOtlpExporter_WithHttpProtobufAndExplicitPath_KeepsEndpoint()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Build());
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };

        ServiceCollectionExtensions.ApplyOtlpExporter(
            exporter, settings, OtlpSignal.Logs, new Uri("http://collector/otlp/v1/logs"));

        Assert.Equal(new Uri("http://collector/otlp/v1/logs"), exporter.Endpoint);
    }

    [Fact]
    public void ApplyOtlpExporter_WithGrpcProtocol_DoesNotAppendSignalPath()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Build());
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.Grpc };

        ServiceCollectionExtensions.ApplyOtlpExporter(
            exporter, settings, OtlpSignal.Logs, new Uri("http://collector:4317"));

        Assert.Equal(new Uri("http://collector:4317"), exporter.Endpoint);
    }

    [Fact]
    public void ApplyOtlpExporter_CarriesHeadersOntoTheExporter()
    {
        IConfiguration configuration = Build(v =>
        {
            v["OpenTelemetry:TracesEndpoint"] = "http://masking-gateway:8102";
            v["OpenTelemetry:TracesHeaders"] = "Authorization=Bearer tok";
        });
        OtlpSettings settings = OtlpSettings.FromConfiguration(configuration);
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };

        ServiceCollectionExtensions.ApplyOtlpExporter(
            exporter, settings, OtlpSignal.Traces,
            OtlpSettings.EndpointFor(configuration, OtlpSignal.Traces));

        Assert.Equal("http://masking-gateway:8102/v1/traces", exporter.Endpoint!.ToString());
        Assert.Contains("Authorization=Bearer tok", exporter.Headers, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyOtlpExporter_LeavesDefaultClient_WhenCertificateVerificationIsOn()
    {
        // The SDK installs its own factory; the platform must not replace it just
        // because no override was requested.
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
        Func<HttpClient>? before = exporter.HttpClientFactory;
        OtlpSettings settings = OtlpSettings.FromConfiguration(Build());

        ServiceCollectionExtensions.ApplyOtlpExporter(
            exporter, settings, OtlpSignal.Traces, new Uri("http://collector:4318"));

        Assert.Same(before, exporter.HttpClientFactory);
    }

    [Fact]
    public void ApplyOtlpExporter_ReplacesClient_WhenInsecureTlsIsOn()
    {
        // A self-signed collector otherwise drops every batch silently: the
        // exporter builds its own client and ignores the deployment TLS policy.
        IConfiguration configuration = Build(v =>
            v["OPENAGENT_ALLOW_INSECURE_TLS"] = "true");
        OtlpSettings settings = OtlpSettings.FromConfiguration(configuration);
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
        Func<HttpClient>? factory = HttpClientSecurity.CreateHttpClientFactory(configuration);

        Assert.NotNull(factory);

        ServiceCollectionExtensions.ApplyOtlpExporter(
            exporter, settings, OtlpSignal.Traces, new Uri("http://collector:4318"), factory);

        Assert.Same(factory, exporter.HttpClientFactory);
    }

    [Fact]
    public void CreateHttpClientFactory_ReturnsNull_WhenVerificationIsOn()
    {
        Assert.Null(HttpClientSecurity.CreateHttpClientFactory(Build()));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void AllowInsecureTls_AcceptsTheValuesDeploymentsActuallySet(string raw, bool expected)
    {
        // GetValue<bool> throws on "1" and on an empty string; a compose file that
        // left the variable blank must mean "off", not a startup failure.
        IConfiguration configuration = Build(v => v["OPENAGENT_ALLOW_INSECURE_TLS"] = raw);

        try
        {
            // The reader falls back to the environment; a leftover value from
            // another test would make this assertion meaningless.
            Environment.SetEnvironmentVariable("OPENAGENT_ALLOW_INSECURE_TLS", null);

            Assert.Equal(expected, HttpClientSecurity.AllowInsecureTls(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAGENT_ALLOW_INSECURE_TLS", null);
        }
    }

    [Fact]
    public void AllowInsecureTls_RejectsGarbage()
    {
        IConfiguration configuration = Build(v => v["OPENAGENT_ALLOW_INSECURE_TLS"] = "maybe");

        Assert.Throws<InvalidOperationException>(() => HttpClientSecurity.AllowInsecureTls(configuration));
    }
}