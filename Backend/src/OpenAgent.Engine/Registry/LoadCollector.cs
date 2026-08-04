namespace OpenAgent.Engine.Registry;

internal sealed class LoadCollector
{
    internal int GetCurrentLoad()
    {
        try
        {
            var load = (int)(GetMemoryPressure() * 0.4
                + GetGcPressure() * 0.3
                + GetThreadPoolPressure() * 0.3);
            return Math.Clamp(load, 0, 100);
        }
        catch
        {
            return 50;
        }
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

    internal int GetGcPressure()
    {
        try
        {
            var collections = GC.CollectionCount(0)
                + GC.CollectionCount(1) * 2
                + GC.CollectionCount(2) * 4;
            return Math.Min(100, collections / 10);
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
            var ioUtilization = maxCompletionPorts > 0
                ? (maxCompletionPorts - completionPorts) * 100 / maxCompletionPorts
                : 0;
            return Math.Clamp(Math.Max(workerUtilization, ioUtilization), 0, 100);
        }
        catch
        {
            return 0;
        }
    }
}
