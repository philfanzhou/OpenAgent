using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class RequestTelemetryMiddlewareTests
{
    private static RequestTelemetryMiddleware CreateMiddleware(
        RequestDelegate next,
        RecordingLogger<RequestTelemetryMiddleware> logger,
        AgentHostOptions? options = null)
        => new(next, Options.Create(options ?? new AgentHostOptions()), logger);

    [Fact]
    public async Task InvokeAsync_EnrichesTraceAndWritesOneCompletionLog()
    {
        var logger = new RecordingLogger<RequestTelemetryMiddleware>();
        var middleware = CreateMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                context.Response.Headers["X-OpenAgent-Selected-Agent-Id"] = "finance";
                return Task.CompletedTask;
            },
            logger);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        using var activity = new Activity("request").Start();

        await middleware.InvokeAsync(context);

        Assert.Equal("unmatched", activity.GetTagItem("openagent.route"));
        Assert.Equal("finance", activity.GetTagItem("openagent.agent.id"));
        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("StatusCode=202", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_FailureIsLoggedAndRethrown()
    {
        var failure = new InvalidOperationException("request failed");
        var logger = new RecordingLogger<RequestTelemetryMiddleware>();
        var middleware = CreateMiddleware(_ => Task.FromException(failure), logger);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(new DefaultHttpContext()));

        Assert.Same(failure, thrown);
        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Contains("StatusCode=500", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/metrics")]
    [InlineData("/METRICS")]
    [InlineData("/metrics/")]
    public async Task InvokeAsync_MetricsScrapePathSkipsTelemetry(string path)
    {
        var logger = new RecordingLogger<RequestTelemetryMiddleware>();
        bool nextInvoked = false;
        var middleware = CreateMiddleware(
            context =>
            {
                nextInvoked = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            logger);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        using var activity = new Activity("request").Start();

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Empty(logger.Entries);
        Assert.Null(activity.GetTagItem("openagent.route"));
        Assert.Null(activity.GetTagItem("openagent.agent.id"));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/HEALTH")]
    [InlineData("/health/")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/report")]
    [InlineData("/ready")]
    public async Task InvokeAsync_HealthProbePathSkipsTelemetry(string path)
    {
        var logger = new RecordingLogger<RequestTelemetryMiddleware>();
        bool nextInvoked = false;
        var middleware = CreateMiddleware(
            context =>
            {
                nextInvoked = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            logger);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        using var activity = new Activity("request").Start();

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Empty(logger.Entries);
        Assert.Null(activity.GetTagItem("openagent.route"));
        Assert.Null(activity.GetTagItem("openagent.agent.id"));
    }

    [Fact]
    public async Task InvokeAsync_ConfiguredProbePathSkipsTelemetry()
    {
        var logger = new RecordingLogger<RequestTelemetryMiddleware>();
        var middleware = CreateMiddleware(
            _ => Task.CompletedTask,
            logger,
            new AgentHostOptions
            {
                HealthCheckLivePath = "/livez",
                HealthCheckReadyPath = "/healthz"
            });
        var context = new DefaultHttpContext();
        context.Request.Path = "/healthz";

        await middleware.InvokeAsync(context);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task InvokeAsync_UnhealthyProbeLogsWarning()
    {
        var logger = new RecordingLogger<RequestTelemetryMiddleware>();
        var middleware = CreateMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            },
            logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/ready";

        await middleware.InvokeAsync(context);

        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("StatusCode=503", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_ProbeFailureIsLoggedAndRethrown()
    {
        var failure = new InvalidOperationException("probe failed");
        var logger = new RecordingLogger<RequestTelemetryMiddleware>();
        var middleware = CreateMiddleware(_ => Task.FromException(failure), logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(context));

        Assert.Same(failure, thrown);
        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
    }

    [Theory]
    [InlineData("http://engine:8080/health")]
    [InlineData("http://engine:8080/health/report")]
    [InlineData("http://engine:8080/ready")]
    [InlineData("http://engine:8080/READY")]
    public void IsHealthProbeUri_MatchesOutboundProbeEndpoints(string url)
    {
        Assert.True(RequestTelemetryMiddleware.IsHealthProbeUri(
            new Uri(url), new AgentHostOptions()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://engine:8080/api/v1/agent/chat")]
    [InlineData("http://engine:8080/healthdash")]
    [InlineData("/ready")]
    public void IsHealthProbeUri_IgnoresBusinessEndpoints(string? url)
    {
        Uri? uri = url == null ? null : new Uri(url, UriKind.RelativeOrAbsolute);

        Assert.False(RequestTelemetryMiddleware.IsHealthProbeUri(
            uri, new AgentHostOptions()));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
