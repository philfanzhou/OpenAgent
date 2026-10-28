using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Serilog.Extensions.Logging;
using Xunit;

namespace OpenAgent.Hosting.Tests;

// Shares OTEL_* environment variables with the other OpenTelemetry tests; serialize them.
[Collection("OtelEnvironment")]
public class SerilogHostBuilderExtensionsTests
{
    [Fact]
    public void UseAgentSerilog_PreservesOpenTelemetryLoggerProviderForOtlpExport()
    {
        using WebApplication app = CreateApp(options => options.EnableOpenTelemetry = true);

        Assert.IsType<SerilogLoggerFactory>(app.Services.GetRequiredService<ILoggerFactory>());
        Assert.Contains(app.Services.GetServices<ILoggerProvider>(), p => p is OpenTelemetryLoggerProvider);
    }

    [Fact]
    public void UseAgentSerilog_WithOpenTelemetryDisabled_DoesNotKeepOtelLoggerProvider()
    {
        using WebApplication app = CreateApp(options => options.EnableOpenTelemetry = false);

        Assert.DoesNotContain(app.Services.GetServices<ILoggerProvider>(), p => p is OpenTelemetryLoggerProvider);
    }

    // Mirrors the Program.cs composition of the Engine/Router hosts: builder.Services calls
    // register the OpenTelemetry logger provider immediately, while builder.Host logging
    // callbacks (ClearProviders) only run during builder.Build().
    private static WebApplication CreateApp(Action<AgentHostOptions> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseAgentSerilog("agent-host-tests");
        builder.Services.AddAgentHost(builder.Configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableJwtAuth = false;
            options.EnableHealthChecks = false;
            configure(options);
        });
        return builder.Build();
    }
}
