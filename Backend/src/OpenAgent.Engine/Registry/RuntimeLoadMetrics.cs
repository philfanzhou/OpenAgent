using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Engine.Registry;

/// <summary>
/// 把聊天运行时的占用暴露为指标：在跑与排队的轮次数、保活进程数。它们是当前值而非累计量，
/// 采集时读取；没有注册任何运行时的组合（纯测试宿主）上报 0。
/// 随宿主启动，保证仪表从进程启动起就可采集。
/// </summary>
internal sealed class RuntimeLoadMetrics : IHostedService, IDisposable
{
    // 与 EngineMeter 同名：宿主的指标导出只订阅这一个 Meter 名。
    private readonly Meter _meter = new("OpenAgent.Engine");
    private readonly IReadOnlyList<IRuntimeLoadSource> _sources;

    public RuntimeLoadMetrics(IEnumerable<IRuntimeLoadSource> sources)
    {
        _sources = sources.ToArray();
        _meter.CreateObservableUpDownCounter(
            "openagent_engine_runtime_active_turns",
            () => Sum(source => source.ActiveTurns),
            description: "Turns that hold a runtime slot and are executing.");
        _meter.CreateObservableUpDownCounter(
            "openagent_engine_runtime_queued_turns",
            () => Sum(source => source.QueuedTurns),
            description: "Turns that arrived and are waiting for a runtime slot.");
        _meter.CreateObservableUpDownCounter(
            "openagent_engine_runtime_kept_processes",
            () => Sum(source => source.KeptProcesses),
            description: "Idle runtime processes kept alive for reuse.");
    }

    internal Meter Meter => _meter;

    private long Sum(Func<IRuntimeLoadSource, int> measure) => _sources.Sum(measure);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();
}
