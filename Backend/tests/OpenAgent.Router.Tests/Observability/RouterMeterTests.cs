using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Xunit;

namespace OpenAgent.Router.Tests.Observability;

public class RouterMeterTests
{
    [Fact]
    public void RecordMethods_EmitNormalizedLowCardinalityTags()
    {
        ConcurrentBag<(string Name, long Value, Dictionary<string, object?> Tags)> measurements = [];
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
        listener.Start();

        RouterMeter.RecordForwardingFailure("", "RequestTimedOut");
        RouterMeter.RecordDiscoveryRefresh("Redis_Error", 0);
        RouterMeter.RecordDiscoverySelection("CHAT", "Static_Fallback");
        RouterMeter.RecordRateLimitDecision(new RateLimitDecision(
            false,
            TimeSpan.FromSeconds(1),
            true,
            "Fail_Closed"));
        RouterMeter.RecordDownstreamProbe("Not_Ready");

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
    }
}
