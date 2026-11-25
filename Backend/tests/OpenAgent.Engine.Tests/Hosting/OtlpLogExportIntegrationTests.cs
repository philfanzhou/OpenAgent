using OpenAgent.Engine.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

/// <summary>
/// End-to-end regression test for OpenTelemetry log export: an ILogger event must travel through
/// the Serilog pipeline (writeToProviders) into the OpenTelemetry logger provider and reach the
/// OTLP endpoint. The exporter defaults to gRPC; forcing http/protobuf lets a plain HTTP listener
/// receive the export and verify the signal path.
/// </summary>
[Collection("OtelEnvironment")]
public class OtlpLogExportIntegrationTests
{
    [Fact]
    public async Task UseAgentSerilog_ForwardsLoggerEventsToOtlpLogsEndpoint()
    {
        await using OtlpTestCollector collector = await OtlpTestCollector.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseAgentSerilog("agent-host-tests");
        builder.Configuration["OpenTelemetry:LogsEndpoint"] = collector.Endpoint;
        builder.Configuration["OpenTelemetry:Protocol"] = "http/protobuf";
        builder.Services.AddAgentHost(builder.Configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableJwtAuth = false;
            options.EnableHealthChecks = false;
            options.ServiceName = "otlp-log-tests";
        });

        await using (var app = builder.Build())
        {
            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("OtlpLogExport")
                .LogInformation("hello otel log export");
        } // Disposing flushes the batch log exporter.

        Assert.Contains(collector.Requests, request => request.Path == "/v1/logs" && request.Body.Length > 0);
    }
}
