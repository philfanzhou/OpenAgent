using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using Xunit;

namespace OpenAgent.Hosting.Tests;

// Shares OTEL_* environment variables with the other OpenTelemetry tests; serialize them.
[Collection("OtelEnvironment")]
public class OtlpEndpointResolutionTests
{
    [Fact]
    public void ResolveOtlpEndpoint_ReturnsNull_WhenNothingIsConfigured()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Assert.Null(ServiceCollectionExtensions.ResolveOtlpEndpoint(configuration));
    }

    [Fact]
    public void ResolveOtlpEndpoint_FallsBackToStandardEnvVar_WhenConfigValueIsBlank()
    {
        // Compose passes OpenTelemetry__OtlpEndpoint as an empty string when the deployment
        // variable is unset; the blank value must not shadow OTEL_EXPORTER_OTLP_ENDPOINT.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:OtlpEndpoint"] = ""
            })
            .Build();

        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4318");

            Assert.Equal(new Uri("http://collector:4318"), ServiceCollectionExtensions.ResolveOtlpEndpoint(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        }
    }

    [Fact]
    public void ResolveOtlpEndpoint_ConfigValueTakesPrecedenceOverEnvVar()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:OtlpEndpoint"] = "http://config-collector:4317"
            })
            .Build();

        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://env-collector:4318");

            Assert.Equal(new Uri("http://config-collector:4317"), ServiceCollectionExtensions.ResolveOtlpEndpoint(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        }
    }

    [Theory]
    [InlineData("not-an-absolute-uri")]
    [InlineData("ftp://collector.example.com")]
    public void ResolveOtlpEndpoint_WithInvalidEnvVarFallback_Throws(string endpoint)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:OtlpEndpoint"] = " "
            })
            .Build();

        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => ServiceCollectionExtensions.ResolveOtlpEndpoint(configuration));

            Assert.Contains("absolute HTTP(S) URI", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        }
    }

    [Fact]
    public void ApplyOtlpEndpoint_WithHttpProtobufAndPathlessEndpoint_AppendsSignalPath()
    {
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };

        ServiceCollectionExtensions.ApplyOtlpEndpoint(exporter, new Uri("http://collector:4318"), "v1/logs");

        Assert.Equal(new Uri("http://collector:4318/v1/logs"), exporter.Endpoint);
    }

    [Fact]
    public void ApplyOtlpEndpoint_WithHttpProtobufAndExplicitPath_KeepsEndpoint()
    {
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };

        ServiceCollectionExtensions.ApplyOtlpEndpoint(exporter, new Uri("http://collector/otlp/v1/logs"), "v1/logs");

        Assert.Equal(new Uri("http://collector/otlp/v1/logs"), exporter.Endpoint);
    }

    [Fact]
    public void ApplyOtlpEndpoint_WithGrpcProtocol_DoesNotAppendSignalPath()
    {
        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.Grpc };

        ServiceCollectionExtensions.ApplyOtlpEndpoint(exporter, new Uri("http://collector:4317"), "v1/logs");

        Assert.Equal(new Uri("http://collector:4317"), exporter.Endpoint);
    }
}
