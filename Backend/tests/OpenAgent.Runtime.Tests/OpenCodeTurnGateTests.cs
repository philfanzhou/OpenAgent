using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

/// <summary>并发闸门的名额与计数：在跑、排队数随进入、离开和取消增减。</summary>
public sealed class OpenCodeTurnGateTests
{
    private static async Task<(OpenCodeTurnGate Gate, OpenCodeProcessKeeper Keeper)> GateAsync(int capacity)
    {
        OpenCodeProcessKeeper keeper = new(
            Options.Create(new OpenCodeOptions { KeepAliveSeconds = 0 }), NullLogger<OpenCodeProcessKeeper>.Instance);
        return (new OpenCodeTurnGate(Options.Create(new OpenCodeOptions { MaxConcurrentTurns = capacity }), keeper), keeper);
    }

    [Fact]
    public async Task EnterAsync_CapacityExhausted_QueuesUntilASlotIsReleased()
    {
        var (gate, keeper) = await GateAsync(1);
        await using (keeper)
        {
            OpenCodeTurnGate.Slot first = await gate.EnterAsync(CancellationToken.None);
            Task<OpenCodeTurnGate.Slot> second = gate.EnterAsync(CancellationToken.None);

            Assert.Equal(1, gate.TurnCapacity);
            Assert.Equal(1, gate.ActiveTurns);
            Assert.Equal(1, gate.QueuedTurns);
            Assert.False(second.IsCompleted);

            first.Dispose();

            OpenCodeTurnGate.Slot queued = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, gate.ActiveTurns);
            Assert.Equal(0, gate.QueuedTurns);
            queued.Dispose();
            Assert.Equal(0, gate.ActiveTurns);
        }
    }

    [Fact]
    public async Task EnterAsync_CancelledWhileQueued_LeavesNoQueuedCountAndKeepsTheSlotAvailable()
    {
        var (gate, keeper) = await GateAsync(1);
        await using (keeper)
        {
            OpenCodeTurnGate.Slot first = await gate.EnterAsync(CancellationToken.None);
            using var waiting = new CancellationTokenSource();
            Task<OpenCodeTurnGate.Slot> second = gate.EnterAsync(waiting.Token);
            Assert.Equal(1, gate.QueuedTurns);

            waiting.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(0, gate.QueuedTurns);
            Assert.Equal(1, gate.ActiveTurns);
            first.Dispose();

            // 被取消的等待没有带走名额：下一个进入者立即拿到。
            (await gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            Assert.Equal(0, gate.ActiveTurns);
        }
    }
}
