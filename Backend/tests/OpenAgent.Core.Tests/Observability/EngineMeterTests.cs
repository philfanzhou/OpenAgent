using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using OpenAgent.Contracts.Requests;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Observability;

[Collection("Engine metrics")]
public class EngineMeterTests
{
    [Fact]
    public void RecordMethods_EmitNamedMetricsWithLowCardinalityTagsAndActualUsage()
    {
        ConcurrentBag<(string Name, long Value, Dictionary<string, object?> Tags)> measurements = [];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == EngineMeter.MeterName)
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

        using (EngineMeter.EngineExecutionMeasurement execution = EngineMeter.StartAgentCall("STREAM"))
        {
            EngineMeter.RecordCapabilityCall("load_skill");
            EngineMeter.RecordCapabilityCall("read_skill_resource");
            EngineMeter.RecordCapabilityCall("customer_lookup");
            execution.Complete(new TokenUsage
            {
                PromptTokens = 12,
                CompletionTokens = 4,
                TotalTokens = 16,
                CachedInputTokens = 3,
                ReasoningTokens = 2
            });
        }
        using (EngineMeter.StartAgentCall("unexpected"))
        {
        }
        EngineMeter.RecordCompression("SUMMARIZE");
        EngineMeter.RecordCompression("custom");

        Assert.Equal(1, Sum(measurements, "openagent_engine_agent_calls_total", "mode", "stream"));
        Assert.Equal(1, Sum(measurements, "openagent_engine_agent_calls_total", "mode", "sync"));
        Assert.Equal(1, Sum(measurements, "openagent_engine_executions_total", "outcome", "success"));
        Assert.Equal(1, Sum(measurements, "openagent_engine_executions_total", "outcome", "failure"));
        Assert.Equal(2, Sum(measurements, "openagent_engine_capability_calls_total", "kind", "skill"));
        Assert.Equal(1, Sum(measurements, "openagent_engine_capability_calls_total", "kind", "tool"));
        Assert.Equal(12, Sum(measurements, "openagent_engine_tokens_total", "type", "input"));
        Assert.Equal(4, Sum(measurements, "openagent_engine_tokens_total", "type", "output"));
        Assert.Equal(16, Sum(measurements, "openagent_engine_tokens_total", "type", "total"));
        Assert.Equal(3, Sum(measurements, "openagent_engine_tokens_total", "type", "cached_input"));
        Assert.Equal(2, Sum(measurements, "openagent_engine_tokens_total", "type", "reasoning"));
        Assert.Equal(1, Sum(measurements, "openagent_engine_compressions_total", "strategy", "summarize"));
        Assert.Equal(1, Sum(measurements, "openagent_engine_compressions_total", "strategy", "other"));
    }

    private static long Sum(
        IEnumerable<(string Name, long Value, Dictionary<string, object?> Tags)> measurements,
        string name,
        string tag,
        string value) => measurements
            .Where(measurement => measurement.Name == name
                && Equals(measurement.Tags[tag], value))
            .Sum(measurement => measurement.Value);
}

[CollectionDefinition("Engine metrics", DisableParallelization = true)]
public sealed class EngineMeterTestCollection
{
}
