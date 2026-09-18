using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OpenAgent.Hosting.Tests;

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
        HttpListener? listener = null;
        var port = 15991;
        while (listener is null && port < 16000)
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener = null;
                port++;
            }
        }

        if (listener is null)
        {
            // No listening permission in this environment; covered by unit tests instead.
            return;
        }

        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

            var builder = WebApplication.CreateBuilder();
            builder.Host.UseAgentSerilog("agent-host-tests");
            builder.Configuration["OpenTelemetry:OtlpEndpoint"] = $"http://127.0.0.1:{port}";
            builder.Services.AddAgentHost(builder.Configuration, options =>
            {
                options.EnableCors = false;
                options.EnableSwagger = false;
                options.EnableJwtAuth = false;
                options.EnableHealthChecks = false;
                options.ServiceName = "otlp-log-tests";
            });

            using (var app = builder.Build())
            {
                app.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OtlpLogExport")
                    .LogInformation("hello otel log export");

                var requestTask = listener.GetContextAsync();
                ((IDisposable)app).Dispose(); // disposing flushes the batch log exporter
                var received = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(15))) == requestTask;

                Assert.True(received, "No OTLP export request reached the endpoint within the timeout.");
                HttpListenerContext context = requestTask.Result;
                Assert.Equal(
                    $"/v1/logs",
                    context.Request.Url?.AbsolutePath,
                    StringComparer.OrdinalIgnoreCase);
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", null);
            listener.Stop();
            listener.Close();
        }
    }
}
