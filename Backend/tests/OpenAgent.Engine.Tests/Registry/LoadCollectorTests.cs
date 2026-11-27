using System.Diagnostics.Metrics;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Engine.Registry;
using Xunit;

namespace OpenAgent.Engine.Tests.Registry;

public sealed class LoadCollectorTests
{
    private sealed class FixedLoadSource(int active, int queued, int capacity) : IRuntimeLoadSource
    {
        public int ActiveTurns => active;
        public int QueuedTurns => queued;
        public int TurnCapacity => capacity;
        public int KeptProcesses => 0;
    }

    [Theory]
    [InlineData(0, 0, 8, 0)]
    [InlineData(3, 0, 8, 37)]
    [InlineData(2, 6, 8, 100)]
    [InlineData(8, 20, 8, 100)]
    public void GetTurnSaturation_DemandOverCapacity(int active, int queued, int capacity, int expected)
    {
        Assert.Equal(expected, new LoadCollector([new FixedLoadSource(active, queued, capacity)]).GetTurnSaturation());
    }

    [Fact]
    public void GetTurnSaturation_SeveralSources_SumsDemandAndCapacity()
    {
        LoadCollector collector = new([new FixedLoadSource(1, 0, 4), new FixedLoadSource(1, 2, 4)]);

        Assert.Equal(50, collector.GetTurnSaturation());
    }

    [Fact]
    public void GetTurnSaturation_NoRuntime_ReportsZero()
    {
        Assert.Equal(0, new LoadCollector().GetTurnSaturation());
        Assert.Equal(0, new LoadCollector([]).GetTurnSaturation());
    }

    [Fact]
    public void GetCurrentLoad_SaturatedRuntime_RaisesTheReportedLoad()
    {
        // 此前 3 个轮次在跑、名额已满时，心跳上报的负载是 1 或 0：宿主压力项几乎为 0，又没有名额信号。
        Assert.Equal(100, new LoadCollector([new FixedLoadSource(8, 0, 8)]).GetCurrentLoad());
        Assert.InRange(new LoadCollector([new FixedLoadSource(3, 0, 8)]).GetCurrentLoad(), 37, 100);
    }
}

public sealed class RuntimeLoadMetricsTests
{
    private sealed class FixedLoadSource(int active, int queued, int kept) : IRuntimeLoadSource
    {
        public int ActiveTurns => active;
        public int QueuedTurns => queued;
        public int TurnCapacity => 8;
        public int KeptProcesses => kept;
    }

    [Fact]
    public void ObservableCounters_ReportTheCurrentRuntimeOccupancy()
    {
        using RuntimeLoadMetrics metrics = new([new FixedLoadSource(3, 2, 5)]);
        Dictionary<string, long> readings = Read(metrics.Meter);

        Assert.Equal(3, readings["openagent_engine_runtime_active_turns"]);
        Assert.Equal(2, readings["openagent_engine_runtime_queued_turns"]);
        Assert.Equal(5, readings["openagent_engine_runtime_kept_processes"]);
    }

    [Fact]
    public void ObservableCounters_NoRuntime_ReportZero()
    {
        using RuntimeLoadMetrics metrics = new([]);
        Dictionary<string, long> readings = Read(metrics.Meter);

        Assert.Equal(0, readings["openagent_engine_runtime_active_turns"]);
        Assert.Equal(0, readings["openagent_engine_runtime_queued_turns"]);
        Assert.Equal(0, readings["openagent_engine_runtime_kept_processes"]);
    }

    /// <summary>只读这个实例的仪表：同一个进程里其它测试宿主可能也建了同名指标。</summary>
    private static Dictionary<string, long> Read(Meter meter)
    {
        using MeterListener listener = new();
        Dictionary<string, long> readings = [];
        listener.InstrumentPublished = (instrument, theListener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name.StartsWith("openagent_engine_runtime_", StringComparison.Ordinal))
            {
                theListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) => readings[instrument.Name] = measurement);
        listener.Start();
        listener.RecordObservableInstruments();
        return readings;
    }
}
