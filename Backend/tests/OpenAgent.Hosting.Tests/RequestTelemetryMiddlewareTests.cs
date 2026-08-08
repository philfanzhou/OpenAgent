using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class RequestTelemetryMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_RecordsRequestMetricsWithoutBusinessInstrumentation()
    {
        ConcurrentBag<(string Name, double Value, Dictionary<string, object?> Tags)> measurements = [];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == "OpenAgent.Hosting")
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, ToDictionary(tags))));
        listener.Start();
        var middleware = new RequestTelemetryMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return Task.CompletedTask;
            },
            NullLogger<RequestTelemetryMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;

        await middleware.InvokeAsync(context);

        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent.requests"
            && measurement.Value == 1
            && Equals(measurement.Tags["http.request.method"], "POST")
            && Equals(measurement.Tags["http.response.status_code"], 202));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent.request.duration"
            && measurement.Value >= 0);
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
}
