using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;
using Xunit;

namespace OpenAgent.Hosting.Tests;

/// <summary>
/// The audit gateway and external collectors are frequently served over HTTPS
/// with a certificate from the company CA. The OTLP exporters build their own
/// HttpClient, so the deployment TLS policy has to be handed to them; without
/// it every export fails validation and is dropped silently while the service
/// still reports healthy.
/// </summary>
[Collection("OtelEnvironment")]
public class OtlpTlsTests
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
    public void CreateHttpClientFactory_IsNullWhenTlsVerificationIsOnByDefault()
    {
        // Null means "leave the SDK default alone", which validates certificates.
        Assert.Null(HttpClientSecurity.CreateHttpClientFactory(Config()));
    }

    [Fact]
    public void CreateHttpClientFactory_ReturnsFactoryWhenInsecureTlsIsEnabled()
    {
        IConfiguration configuration = Config(("OPENAGENT_ALLOW_INSECURE_TLS", "true"));

        Func<HttpClient>? factory = HttpClientSecurity.CreateHttpClientFactory(configuration);

        Assert.NotNull(factory);
        using HttpClient client = factory!.Invoke();
        Assert.NotNull(client);
    }

    [Fact]
    public void CreateHttpClientFactory_EachInvocationIsIndependent()
    {
        // A shared handler that gets disposed would break the next exporter.
        IConfiguration configuration = Config(("OPENAGENT_ALLOW_INSECURE_TLS", "true"));
        Func<HttpClient> factory = HttpClientSecurity.CreateHttpClientFactory(configuration)!;

        HttpClient first = factory.Invoke();
        first.Dispose();

        using HttpClient second = factory.Invoke();
        Assert.NotNull(second);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AllowInsecureTls_ReadsTheDeploymentSwitch(string? value, bool expected)
    {
        Assert.Equal(expected, HttpClientSecurity.AllowInsecureTls(Config(("OPENAGENT_ALLOW_INSECURE_TLS", value))));
    }

    [Fact]
    public void OtlpExporter_AcceptsAnHttpsEndpoint()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(
            Config(("OpenTelemetry:TracesEndpoint", "https://agent.monolithicpower.com:444/audit-otlp")));

        Assert.Equal(
            "https://agent.monolithicpower.com:444/audit-otlp",
            settings.EndpointFor(OtlpSignal.Traces));
    }

    [Fact]
    public void HttpsEndpointWithNoPath_GetsTheSignalPathAppended()
    {
        // An https endpoint behind a proxy must behave like an http one.
        OtlpSettings settings = OtlpSettings.FromConfiguration(Config(
            ("OpenTelemetry:TracesEndpoint", "https://collector.example.com"),
            ("OpenTelemetry:Protocol", "http/protobuf")));

        Assert.Equal("https://collector.example.com", settings.EndpointFor(OtlpSignal.Traces));

        var exporter = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
        ServiceCollectionExtensions.ApplyOtlpEndpoint(
            exporter, new Uri("https://collector.example.com"), "v1/traces");

        Assert.Equal(new Uri("https://collector.example.com/v1/traces"), exporter.Endpoint);
    }

    [Fact]
    public void BearerHeaderSurvivesAnHttpsEndpoint()
    {
        OtlpSettings settings = OtlpSettings.FromConfiguration(Config(
            ("OpenTelemetry:TracesEndpoint", "https://agent.monolithicpower.com:444/audit-otlp/v1/traces"),
            ("OpenTelemetry:TracesHeaders", "Authorization=Bearer audit-token"),
            ("OpenTelemetry:Protocol", "http/protobuf")));

        Assert.Equal("Authorization=Bearer audit-token", settings.HeadersFor(OtlpSignal.Traces));
    }

    [Theory]
    [InlineData("https://agent.monolithicpower.com:444")]
    [InlineData("https://agent.monolithicpower.com:444/audit-otlp")]
    [InlineData("http://masking-gateway:8102")]
    [InlineData("http://10.51.4.45:8102")]
    public void EndpointsUsedInDeployment_AreAccepted(string endpoint)
    {
        // Both the public HTTPS form and the direct container/network form must
        // parse; the deployment chooses one without touching code.
        Uri parsed = OtlpSettings.ParseEndpoint(endpoint, OtlpSignal.Traces);
        Assert.Equal(new Uri(endpoint), parsed);
    }
}