using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class RequestTelemetryMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_EnrichesTraceAndWritesOneCompletionLog()
    {
        var logger = new RecordingLogger<RequestTelemetryMiddleware>();
        var middleware = new RequestTelemetryMiddleware(
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
        var middleware = new RequestTelemetryMiddleware(
            _ => Task.FromException(failure),
            logger);

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
        var middleware = new RequestTelemetryMiddleware(
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
