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
        Assert.Contains(measurements, measurement =>
            measurement.Name == "openagent_router_forwarding_failures_total"
            && measurement.Value == 1
            && Equals(measurement.Tags["action"], "unknown")
            && Equals(measurement.Tags["forwarder_error"], "requesttimedout"));
    }
}
