using System.Diagnostics.Metrics;
using System.Reflection;
using Xunit;

namespace OpenAgent.Core.Tests.Observability;

public class AgentMeterTests
{
    [Fact]
    public void AgentMeter_PublishesLowCardinalityRequestAndToolMetrics()
    {
        var meterType = Type.GetType("OpenAgent.Core.Observability.AgentMeter, OpenAgent.Core");
        Assert.NotNull(meterType);

        var observed = new HashSet<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OpenAgent.Core")
                {
                    observed.Add(instrument.Name);
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            observed.Add(instrument.Name);
            AssertNoHighCardinalityTags(tags);
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            observed.Add(instrument.Name);
            AssertNoHighCardinalityTags(tags);
        });
        listener.Start();

        Invoke(meterType!, "RecordRequest", "chat-sse", "success", true, 123.4);
        Invoke(meterType!, "RecordFailure", "tool", "McpToolExecutionFailed");
        Invoke(meterType!, "RecordTurn", 42.0, true);
        Invoke(meterType!, "RecordFirstToken", 77.0);
        Invoke(meterType!, "RecordToolCall", "mcp", "success", 12.0);
        Invoke(meterType!, "RecordTurnsPerRequest", 2);

        Assert.Contains("openagent_requests_total", observed);
        Assert.Contains("openagent_failures_total", observed);
        Assert.Contains("openagent_request_duration_ms", observed);
        Assert.Contains("openagent_turn_duration_ms", observed);
        Assert.Contains("openagent_model_first_token_ms", observed);
        Assert.Contains("openagent_tool_calls_total", observed);
        Assert.Contains("openagent_tool_duration_ms", observed);
        Assert.Contains("openagent_turns_per_request", observed);
    }

    private static void Invoke(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, args);
    }

    private static void AssertNoHighCardinalityTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            Assert.False(tag.Key is "trace_id" or "conversation_id" or "user_id", $"Unexpected high-cardinality metric tag: {tag.Key}");
        }
    }
}
