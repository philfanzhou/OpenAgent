using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Engine.Registry;

/// <summary>
/// 随心跳上报的 Engine 负载（0–100）。取两类信号里较大的一个：聊天运行时的名额饱和度
/// （在跑加排队的轮次占名额容量的比例），以及宿主自身的压力（内存、GC、线程池）。
/// </summary>
internal sealed class LoadCollector(IReadOnlyList<IRuntimeLoadSource>? runtimeLoad = null)
{
    private readonly IReadOnlyList<IRuntimeLoadSource> _runtimeLoad = runtimeLoad ?? [];

    internal int GetCurrentLoad()
    {
        try
        {
            var hostPressure = (int)(GetMemoryPressure() * 0.4
                + GetGcPressure() * 0.3
                + GetThreadPoolPressure() * 0.3);
            return Math.Clamp(Math.Max(hostPressure, GetTurnSaturation()), 0, 100);
        }
        catch
        {
            return 50;
        }
    }

    /// <summary>在跑加排队的轮次占名额容量的比例：开始排队就越接近 100，满了就是 100。</summary>
    internal int GetTurnSaturation()
    {
        int capacity = 0;
        int demand = 0;
        foreach (IRuntimeLoadSource source in _runtimeLoad)
        {
            capacity += source.TurnCapacity;
            demand += source.ActiveTurns + source.QueuedTurns;
        }
        return capacity > 0
            ? Math.Min(100, (int)((long)demand * 100 / capacity))
            : 0;
    }

    internal int GetMemoryPressure()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            var totalMemory = gcInfo.TotalAvailableMemoryBytes;
            var usedMemory = GC.GetTotalMemory(false);
            return totalMemory > 0
                ? Math.Clamp((int)(usedMemory * 100 / totalMemory), 0, 100)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>GC 压力是当前信号：自上次垃圾回收以来花在暂停上的时间占比，不是进程启动以来的累计次数。</summary>
    internal int GetGcPressure()
    {
        try
        {
            return Math.Clamp((int)GC.GetGCMemoryInfo().PauseTimePercentage, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    internal int GetThreadPoolPressure()
    {
        try
        {
            ThreadPool.GetAvailableThreads(out var workers, out var completionPorts);
            ThreadPool.GetMaxThreads(out var maxWorkers, out var maxCompletionPorts);
            var workerUtilization = maxWorkers > 0 ? (maxWorkers - workers) * 100 / maxWorkers : 0;
            var ioUtilization = maxCompletionPorts > 0 ? (maxCompletionPorts - completionPorts) * 100 / maxCompletionPorts : 0;
            return Math.Clamp(Math.Max(workerUtilization, ioUtilization), 0, 100);
        }
        catch
        {
            return 0;
        }
    }
}
