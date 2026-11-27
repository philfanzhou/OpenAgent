namespace OpenAgent.Contracts.Runtime;

/// <summary>
/// 一个 Engine 上聊天运行时的占用：正在执行和等待名额的轮次数、名额容量，以及保活进程数。
/// Engine 随心跳上报，Router 据此优先选择较空闲的实例；同一份数值也作为指标暴露。
/// </summary>
public interface IRuntimeLoadSource
{
    /// <summary>已拿到名额、正在执行的轮次数。</summary>
    int ActiveTurns { get; }

    /// <summary>已到达、还在等待名额的轮次数。</summary>
    int QueuedTurns { get; }

    /// <summary>可同时执行的轮次上限。</summary>
    int TurnCapacity { get; }

    /// <summary>保活池中空闲的运行时进程数。它们不占名额，但每个仍驻留内存。</summary>
    int KeptProcesses { get; }
}
