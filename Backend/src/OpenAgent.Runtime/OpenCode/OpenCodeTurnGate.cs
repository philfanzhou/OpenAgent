using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>
/// 轮次并发闸门：同一时刻最多 <see cref="MaxConcurrentTurns"/> 个轮次执行，其余排队等待。
/// 闸门同时记录在跑与排队数，供心跳上报负载和指标使用。
/// </summary>
internal sealed class OpenCodeTurnGate : IRuntimeLoadSource, IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly OpenCodeProcessKeeper _keeper;
    private readonly int _capacity;
    private int _active;
    private int _queued;

    public OpenCodeTurnGate(IOptions<OpenCodeOptions> options, OpenCodeProcessKeeper keeper)
    {
        _capacity = options.Value.MaxConcurrentTurns;
        _slots = new SemaphoreSlim(_capacity, _capacity);
        _keeper = keeper;
    }

    public int ActiveTurns => Volatile.Read(ref _active);
    public int QueuedTurns => Volatile.Read(ref _queued);
    public int TurnCapacity => _capacity;
    public int KeptProcesses => _keeper.KeptCount;

    /// <summary>等待名额；返回的句柄释放时归还名额。</summary>
    internal async Task<Slot> EnterAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _queued);
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _queued);
            throw;
        }
        Interlocked.Decrement(ref _queued);
        Interlocked.Increment(ref _active);
        return new Slot(this);
    }

    private void Leave()
    {
        Interlocked.Decrement(ref _active);
        _slots.Release();
    }

    public void Dispose() => _slots.Dispose();

    internal sealed class Slot(OpenCodeTurnGate gate) : IDisposable
    {
        public void Dispose() => gate.Leave();
    }
}
