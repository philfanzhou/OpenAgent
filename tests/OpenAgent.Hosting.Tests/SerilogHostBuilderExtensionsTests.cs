using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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

    // -- console format ---------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Plain")]
    [InlineData("plain")]
    public void IsJsonConsole_DefaultsToPlain(string? value)
    {
        Assert.False(SerilogHostBuilderExtensions.IsJsonConsole(Config("Serilog:ConsoleFormat", value)));
    }

    [Theory]
    [InlineData("Json")]
    [InlineData("json")]
    [InlineData(" JSON ")]
    public void IsJsonConsole_AcceptsJson(string value)
    {
        Assert.True(SerilogHostBuilderExtensions.IsJsonConsole(Config("Serilog:ConsoleFormat", value)));
    }

    [Fact]
    public void IsJsonConsole_UnknownValueFailsFast()
    {
        // A typo must not silently fall back to plain text: that is exactly how
        // the log fields disappear from the collector.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SerilogHostBuilderExtensions.IsJsonConsole(Config("Serilog:ConsoleFormat", "Jsno")));

        Assert.Contains("Jsno", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HasConsoleFormatterArgument_DetectsConfiguredFormatter()
    {
        IConfiguration withFormatter = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:0:Name"] = "Console",
                ["Serilog:WriteTo:0:Args:formatter"] = "Serilog.Formatting.Json.JsonFormatter, Serilog"
            }).Build();

        IConfiguration withoutFormatter = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Serilog:WriteTo:0:Name"] = "Console" }).Build();

        Assert.True(SerilogHostBuilderExtensions.HasConsoleFormatterArgument(withFormatter));
        Assert.False(SerilogHostBuilderExtensions.HasConsoleFormatterArgument(withoutFormatter));
    }

    [Fact]
    public void RepositoryAppSettings_EnableJsonConsoleWithFormatter()
    {
        // Guards the shipped configuration: the services' appsettings.json must
        // keep emitting JSON with a formatter (no duplicate plain-text lines),
        // otherwise Promtail cannot extract TraceId/ConversationId.
        foreach (string file in new[] { "OpenAgent.Engine.Host", "OpenAgent.Router" })
        {
            IConfiguration configuration = LoadAppSettings(file);

            Assert.True(
                SerilogHostBuilderExtensions.IsJsonConsole(configuration),
                $"{file}/appsettings.json must set Serilog:ConsoleFormat=Json.");
            Assert.True(
                SerilogHostBuilderExtensions.HasConsoleFormatterArgument(configuration),
                $"{file}/appsettings.json must configure the Console formatter argument.");
        }
    }

    private static IConfiguration LoadAppSettings(string project)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "OpenAgent.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        string path = Path.Combine(root!, "src", project, "appsettings.json");
        Assert.True(File.Exists(path), $"Missing {path}");

        return new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
    }

    private static IConfiguration Config(string key, string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
}
