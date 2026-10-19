using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;
using Xunit.Sdk;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>
/// Batch shutdown convergence (F1 regressions): a batch with a request in flight must settle
/// its slots, its assembly task and disposal on every close path, leaving no unobserved task.
/// Disposal is asserted through bounded waits instead of `await using`, so a convergence
/// regression FAILS at its deadline instead of hanging the runner's own teardown.
/// </summary>
public sealed class AcpRpcBatchShutdownTests
{
    [Fact]
    public async Task Batch_InFlightHandler_ClientClosesFirst_AgentDisposalConverges()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        Task handlerStarted = StartHangingHandler(agent);

        client.Start();
        agent.Start();

        await RpcTestHarness.SendFrameAsync(clientWire, """
            [{"id":"b1","method":"test/hang","params":{}},{"method":"test/note"}]
            """);
        await AwaitWithinAsync(handlerStarted, test, "the batch handler start");

        await AwaitWithinAsync(client.DisposeAsync().AsTask(), test, "the client disposal");
        await AwaitWithinAsync(agent.DisposeAsync().AsTask(), test, "the agent disposal after the peer closed first");
        // A closing link writes nothing, batch or single; and no task stays behind.
        await EngineTestUtil.AssertNoFrameAsync(agentWire, test.Token);
        await AssertTrackedDrainedAsync(agent, test);
    }

    [Fact]
    public async Task Batch_InFlightHandler_AgentDirectDispose_Converges()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        Task handlerStarted = StartHangingHandler(agent);

        client.Start();
        agent.Start();

        await RpcTestHarness.SendFrameAsync(clientWire, """
            [{"id":"b1","method":"test/hang","params":{}},{"method":"test/note"}]
            """);
        await AwaitWithinAsync(handlerStarted, test, "the batch handler start");

        await AwaitWithinAsync(agent.DisposeAsync().AsTask(), test, "the agent disposal with a batch handler in flight");
        await AwaitWithinAsync(client.DisposeAsync().AsTask(), test, "the client disposal");
        await EngineTestUtil.AssertNoFrameAsync(agentWire, test.Token);
        await AssertTrackedDrainedAsync(agent, test);
    }

    [Fact]
    public async Task Batch_DisposalRacesAheadOfAssemblyTracking_NoTrackedTaskLeaks()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, _) = RpcTestHarness.Connect();
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        TaskCompletionSource readerParked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposeObserved = 0;

        // The handler's synchronous prefix parks the READER thread inside the batch dispatch, so
        // the first shutdown snapshot cannot contain the assembly task. Waking into the lifetime
        // cancellation leaves the slot unfilled unless shutdown settles it, which is exactly the
        // leak interleaving: disposal returns while the assembly task stays tracked forever.
        agent.RegisterRequestHandler("test/park", (_, ct) =>
        {
            readerParked.TrySetResult();
            SpinWait.SpinUntil(() => Volatile.Read(ref disposeObserved) == 1, RpcTestHarness.TimeoutMs);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(static writer =>
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }));
        });
        client.Start();
        agent.Start();

        await RpcTestHarness.SendFrameAsync(clientWire, """
            [{"id":"b1","method":"test/park","params":{}},{"method":"test/note"}]
            """);
        await AwaitWithinAsync(readerParked.Task, test, "the reader parking inside the batch dispatch");

        Task disposal = agent.DisposeAsync().AsTask();
        Volatile.Write(ref disposeObserved, 1);
        await AwaitWithinAsync(disposal, test, "the disposal that raced ahead of assembly tracking");
        await AssertTrackedDrainedAsync(agent, test);
        await AwaitWithinAsync(client.DisposeAsync().AsTask(), test, "the client disposal");
    }

    [Fact]
    public async Task Batch_ShutdownRace_RepeatedRounds_ConvergeWithNoLeak()
    {
        // Dispose racing the batch dispatch, handler start and assembly tracking in whatever
        // order the scheduler picks: every round must converge and drain.
        for (int round = 0; round < 40; round++)
        {
            (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, _) = RpcTestHarness.Connect();
            using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
            _ = StartHangingHandler(agent);
            client.Start();
            agent.Start();

            await RpcTestHarness.SendFrameAsync(clientWire, """
                [{"id":"b1","method":"test/hang","params":{}},{"method":"test/note"}]
                """);
            await AwaitWithinAsync(client.DisposeAsync().AsTask(), test, $"round {round}: the client disposal");
            await AwaitWithinAsync(agent.DisposeAsync().AsTask(), test, $"round {round}: the agent disposal");
            await AssertTrackedDrainedAsync(agent, test);
            await AssertTrackedDrainedAsync(client, test);
        }
    }

    [Fact]
    public async Task SingleRequest_ShutdownDuringHandler_StaysSilentAndConverges()
    {
        // Control: the single-request shutdown path keeps its existing semantics — no response
        // frame on a closing link, pending call fails closed, disposal converges.
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        Task handlerStarted = StartHangingHandler(agent);
        client.Start();
        agent.Start();

        Task<JsonElement> pending = client.RequestAsync("test/hang", null, test.Token);
        await AwaitWithinAsync(handlerStarted, test, "the single-request handler start");

        await AwaitWithinAsync(client.DisposeAsync().AsTask(), test, "the client disposal");
        await AwaitWithinAsync(Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending), test, "the pending call failing closed");
        await AwaitWithinAsync(agent.DisposeAsync().AsTask(), test, "the agent disposal");
        await EngineTestUtil.AssertNoFrameAsync(agentWire, test.Token);
        await AssertTrackedDrainedAsync(agent, test);
    }

    /// <summary>Registers an agent handler that honors its lifetime token and never finishes on its own.</summary>
    private static Task StartHangingHandler(AcpRpcConnection agent)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.RegisterRequestHandler("test/hang", async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return EngineTestUtil.BuildElement(static writer =>
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            });
        });
        return started.Task;
    }

    /// <summary>Asserts a wait settles within the test deadline; a regression fails instead of hanging the run.</summary>
    private static async Task AwaitWithinAsync(Task task, CancellationTokenSource test, string what)
    {
        Task done = await Task.WhenAny(task, Task.Delay(RpcTestHarness.TimeoutMs, test.Token));
        if (!ReferenceEquals(done, task)) throw new XunitException($"{what} did not converge within the test deadline.");
        await task;
    }

    private static async Task AssertTrackedDrainedAsync(AcpRpcConnection connection, CancellationTokenSource test) =>
        await EngineTestUtil.WaitForAsync(() => connection.TrackedTaskCount == 0, test.Token);
}
