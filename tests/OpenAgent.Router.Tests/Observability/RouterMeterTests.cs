using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Xunit;

namespace OpenAgent.Router.Tests.Observability;

[Collection("Router metrics")]
public class RouterMeterTests
{
    [Fact]
    public void RecordMethods_EmitNormalizedLowCardinalityTags()
    {
        ConcurrentBag<(string Name, long Value, Dictionary<string, object?> Tags)> measurements = [];
        ConcurrentBag<(string Name, double Value, Dictionary<string, object?> Tags)> durations = [];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == RouterMeter.MeterName)
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Add((
                instrument.Name,
                value,
                tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            durations.Add((
                instrument.Name,
                value,
                tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        });
        listener.Start();

        RouterMeter.RecordRequest("STREAM");
        RouterMeter.RecordRequest("stream");
        RouterMeter.RecordForward("stream", succeeded: true);
        RouterMeter.RecordForward("stream", succeeded: false);
        RouterMeter.RecordSseCompletion("stream", TimeSpan.FromSeconds(2), succeeded: true);
        RouterMeter.RecordForwardingFailure("", "RequestTimedOut");
        RouterMeter.RecordDiscoveryRefresh("Redis_Error", 0);
        RouterMeter.RecordDiscoverySelection("CHAT", "Static_Fallback");
        RouterMeter.RecordRateLimitDecision(new RateLimitDecision(
            false,
            TimeSpan.FromSeconds(1),
            true,
            "Fail_Closed"));
        RouterMeter.RecordDownstreamProbe("Not_Ready");
        RouterMeter.RecordProviderSelection("Explicit");
        RouterMeter.RecordAclDenial();
        RouterMeter.RecordCacheHit("Query");

        Assert.Equal(2, measurements
            .Where(measurement => measurement.Name == "openagent_router_requests_total"
                && Equals(measurement.Tags["action"], "stream"))
            .Sum(measurement => measurement.Value));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_forwards_total"
            && Equals(measurement.Tags["action"], "stream")
            && Equals(measurement.Tags["outcome"], "success"));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_forwards_total"
            && Equals(measurement.Tags["outcome"], "failure"));
        Assert.Contains(durations, measurement =>
            measurement.Name == "openagent_router_sse_duration_seconds"
            && measurement.Value == 2
            && Equals(measurement.Tags["action"], "stream")
            && Equals(measurement.Tags["outcome"], "success"));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_forwarding_failures_total"
            && measurement.Value == 1
            && Equals(measurement.Tags["action"], "unknown")
            && Equals(measurement.Tags["forwarder_error"], "requesttimedout"));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_discovery_refresh_total"
            && Equals(measurement.Tags["outcome"], "redis_error"));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_discovery_selections_total"
            && Equals(measurement.Tags["intent"], "chat")
            && Equals(measurement.Tags["source"], "static_fallback"));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_rate_limit_decisions_total"
            && Equals(measurement.Tags["outcome"], "denied")
            && Equals(measurement.Tags["source"], "fail_closed")
            && Equals(measurement.Tags["degraded"], true));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_downstream_probes_total"
            && Equals(measurement.Tags["outcome"], "not_ready"));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_provider_selections_total"
            && Equals(measurement.Tags["source"], "explicit"));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_acl_denials_total"
            && Equals(measurement.Tags["reason"], "agent_acl"));
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_cache_hits_total"
            && Equals(measurement.Tags["cache"], "query"));
    }
}

[CollectionDefinition("Router metrics", DisableParallelization = true)]
public sealed class RouterMeterTestCollection
{
}
