using System.Text.Json;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;
using Xunit.Sdk;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>
/// Shutdown with an outbound call on the wire must converge within one grace period. A close
/// once raced the cancel flow: FailAllPending failed the pending call without a terminal
/// state, the racing StartCancelFlow re-armed a grace nobody would cancel, and disposal
/// stalled for a full CancelGracePeriod (review F2). These regressions hold every close path
/// to a deadline one second inside the 5 s default grace: the old defect measured ~5.0 s per
/// affected round and fails here, while the healthy path converges in milliseconds even when
/// a full solution run shares the machine with other test hosts — the 10 s harness budget is
/// what used to mask the stall.
/// </summary>
public sealed class AcpRpcShutdownConvergenceTests
{
    private static readonly TimeSpan TightDeadline = TimeSpan.FromSeconds(4);

    private const int Rounds = 30;

    [Fact]
    public async Task DisposeClose_WithInFlightOutboundCall_ConvergesWithinTightDeadline()
    {
        for (int round = 0; round < Rounds; round++)
        {
            (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, _) = RpcTestHarness.Connect();
            using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
            (Task<JsonElement> pending, Task started) = StartInFlightCall(client, agent, clientWire, test.Token);
            await started.WaitAsync(test.Token);

            await AssertConvergesWithinAsync(client.DisposeAsync().AsTask(), test, $"round {round}: the disposal");
            await AssertConvergesWithinAsync(client.Completion, test, $"round {round}: the completion");
            await AssertConvergesWithinAsync(
                Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending), test, $"round {round}: the pending call");
        }
    }

    [Fact]
    public async Task EofClose_WithInFlightOutboundCall_ConvergesWithinTightDeadline()
    {
        for (int round = 0; round < Rounds; round++)
        {
            (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, _) = RpcTestHarness.Connect();
            using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
            (Task<JsonElement> pending, Task started) = StartInFlightCall(client, agent, clientWire, test.Token);
            await started.WaitAsync(test.Token);

            clientWire.SimulateEof();
            await AssertConvergesWithinAsync(client.Completion, test, $"round {round}: the completion after EOF");
            await AssertConvergesWithinAsync(
                Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending), test, $"round {round}: the pending call after EOF");
            await AssertConvergesWithinAsync(client.DisposeAsync().AsTask(), test, $"round {round}: the disposal after EOF");
        }
    }

    [Fact]
    public async Task FaultClose_WithInFlightOutboundCall_ConvergesWithinTightDeadline()
    {
        for (int round = 0; round < Rounds; round++)
        {
            (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, _) = RpcTestHarness.Connect();
            using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
            (Task<JsonElement> pending, Task started) = StartInFlightCall(client, agent, clientWire, test.Token);
            await started.WaitAsync(test.Token);

            clientWire.Fault(new IOException("the loopback faulted"));
            // A terminal transport fault stays observable on Completion; convergence is what this asserts.
            await AssertConvergesWithinAsync(
                Assert.ThrowsAnyAsync<Exception>(() => client.Completion), test, $"round {round}: the faulted completion");
            await AssertConvergesWithinAsync(
                Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending), test, $"round {round}: the pending call after the fault");
            await AssertConvergesWithinAsync(client.DisposeAsync().AsTask(), test, $"round {round}: the disposal after the fault");
        }
    }

    /// <summary>Puts one request on the wire and returns it with the signal of the agent handler running it.</summary>
    private static (Task<JsonElement> Pending, Task Started) StartInFlightCall(
        AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, CancellationToken deadline)
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
        client.Start();
        agent.Start();

        // The handler only starts after the agent read the frame, so the started signal proves
        // the request is on the wire and its caller is parked on the pending response.
        return (client.RequestAsync("test/hang", null, deadline), started.Task);
    }

    /// <summary>Asserts a wait settles inside the tight deadline; a stalled convergence fails instead of waiting out a grace period.</summary>
    private static async Task AssertConvergesWithinAsync(Task task, CancellationTokenSource test, string what)
    {
        Task done = await Task.WhenAny(task, Task.Delay(TightDeadline, test.Token)).ConfigureAwait(false);
        if (!ReferenceEquals(done, task))
        {
            throw new XunitException($"{what} did not converge within {TightDeadline.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s.");
        }

        await task.ConfigureAwait(false);
    }
}
