using Microsoft.Extensions.Options;
using OpenAgent.Application.Execution.Admission;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using Xunit;

namespace OpenAgent.Application.Tests.Execution.Admission;

public sealed class TurnAdmissionTests
{
    [Fact]
    public async Task Enter_SaturatedTenant_DoesNotBlockEligibleTenantAndPreservesFifo()
    {
        TurnAdmission admission = Create(concurrent: 2, perTenant: 1);
        using AdmissionTicket a = admission.Enter("a", default);
        using AdmissionTicket b = admission.Enter("b", default);
        using AdmissionTicket a1 = admission.Enter("a", default);
        using AdmissionTicket c = admission.Enter("c", default);
        using AdmissionTicket a2 = admission.Enter("a", default);
        b.Dispose();
        await c.Ready.Task;
        Assert.False(a1.Ready.Task.IsCompleted);
        a.Dispose();
        await a1.Ready.Task;
        Assert.False(a2.Ready.Task.IsCompleted);
        a1.Dispose();
        await a2.Ready.Task;
    }

    [Theory]
    [InlineData(1, 3)] // Host queue
    [InlineData(3, 1)] // tenant queue
    [InlineData(0, 0)] // no waiting
    public void Enter_QueueLimit_RejectsBeforeAccepting(int queue, int tenantQueue)
    {
        TurnAdmission admission = Create(queue: queue, tenantQueue: tenantQueue);
        using AdmissionTicket active = admission.Enter("a", default);
        List<AdmissionTicket> waiting = [];
        try
        {
            // Effective bound is the lower of Host and tenant queue limits.
            for (int i = 0; i < Math.Min(queue, tenantQueue); i++) waiting.Add(admission.Enter("a", default));
            RuntimeAdmissionException error = Assert.Throws<RuntimeAdmissionException>(() => admission.Enter("a", default));
            Assert.Equal(AgentErrorCode.RuntimeQueueFull, error.ErrorCode);
            Assert.Equal(1, error.RetryAfterSeconds);
        }
        finally
        {
            foreach (AdmissionTicket ticket in waiting) ticket.Dispose();
        }
    }

    [Fact]
    public async Task Enter_QueueDeadline_RemovesWaiterAndAllowsReplacement()
    {
        TurnAdmission admission = Create(queue: 1, tenantQueue: 1, timeout: 1);
        using AdmissionTicket active = admission.Enter("a", default);
        using AdmissionTicket expired = admission.Enter("b", default);
        RuntimeAdmissionException error = await Assert.ThrowsAsync<RuntimeAdmissionException>(() => expired.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(AgentErrorCode.RuntimeQueueTimeout, error.ErrorCode);
        using AdmissionTicket next = admission.Enter("c", default);
        Assert.True(next.WasQueued);
        active.Dispose();
        await next.Ready.Task;
    }

    [Fact]
    public async Task Enter_CancelQueued_RemovesImmediatelyButActiveCancelWaitsForCleanup()
    {
        TurnAdmission admission = Create(queue: 1, tenantQueue: 1);
        using CancellationTokenSource running = new();
        using AdmissionTicket active = admission.Enter("a", running.Token);
        using CancellationTokenSource queued = new();
        using AdmissionTicket cancelled = admission.Enter("b", queued.Token);
        queued.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Ready.Task);
        using AdmissionTicket next = admission.Enter("c", default);
        running.Cancel();
        Assert.False(next.Ready.Task.IsCompleted); // active resources still owned
        active.Dispose();
        await next.Ready.Task;
    }

    [Fact]
    public async Task Enter_CancelAndReleaseRace_RecoversAllCapacity()
    {
        TurnAdmission admission = Create(concurrent: 4, perTenant: 2, queue: 512, tenantQueue: 512);
        using SemaphoreSlim ready = new(0);
        using SemaphoreSlim finish = new(0);
        using CancellationTokenSource cancel = new();
        int active = 0, peak = 0;
        Task[] tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            using AdmissionTicket ticket = admission.Enter("tenant-" + i % 4, i % 2 == 0 ? cancel.Token : default);
            try
            {
                await ticket.Ready.Task;
                int current = Interlocked.Increment(ref active);
                int old;
                do { old = peak; } while (current > old && Interlocked.CompareExchange(ref peak, current, old) != old);
                ready.Release();
                await finish.WaitAsync();
                Interlocked.Decrement(ref active);
            }
            catch (OperationCanceledException) { }
        }).ToArray();
        for (int i = 0; i < 4; i++) await ready.WaitAsync();
        cancel.Cancel();
        finish.Release(200);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, peak);
        List<AdmissionTicket> recovered = [];
        try
        {
            for (int i = 0; i < 4; i++)
            {
                AdmissionTicket ticket = admission.Enter("recovered-" + i, default);
                recovered.Add(ticket);
                Assert.False(ticket.WasQueued);
            }
        }
        finally { foreach (AdmissionTicket ticket in recovered) ticket.Dispose(); }
    }

    private static TurnAdmission Create(int concurrent = 1, int perTenant = 1, int queue = 8, int tenantQueue = 8, int timeout = 30) =>
        new(Options.Create(new AdmissionOptions
        {
            MaxConcurrentTurns = concurrent, MaxConcurrentPerTenant = perTenant,
            MaxQueuedTurns = queue, MaxQueuedPerTenant = tenantQueue, QueueTimeoutSeconds = timeout
        }));
}
