using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using OpenAgent.Runtime.Acp.Rpc;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>General request cancellation (V09/V10): wire cancel, grace, peer cancel, and independence from slow handlers.</summary>
public sealed class AcpRpcCancellationTests
{
    [Fact]
    public async Task Request_CancelAfterWrite_PeerCancelsAndAnswersCancelled()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect(
            new AcpRpcOptions { CancelGracePeriod = TimeSpan.FromSeconds(10) });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        TaskCompletionSource handlerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancellationSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.RegisterRequestHandler("test/long", async (_, ct) =>
        {
            handlerStarted.TrySetResult();
            ct.Register(() => cancellationSeen.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
        });
        client.Start();
        agent.Start();

        using CancellationTokenSource caller = new();
        Task<JsonElement> call = client.RequestAsync("test/long", null, caller.Token);
        await handlerStarted.Task.ConfigureAwait(false);
        await caller.CancelAsync().ConfigureAwait(false);

        // The engine sends $/cancel_request; the cooperating handler observes it and answers cancelled.
        await cancellationSeen.Task.ConfigureAwait(false);
        AcpRpcException failure = await Assert.ThrowsAsync<AcpRpcException>(() => call).ConfigureAwait(false);
        Assert.Equal(ErrorCode.RequestCancelled, failure.Code);
    }

    [Fact]
    public async Task Request_CancelUnconfirmed_FailsCallAndClosesConnection()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect(
            new AcpRpcOptions { CancelGracePeriod = TimeSpan.FromMilliseconds(150) });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The peer swallows cancellation and never answers within the grace period.
        agent.RegisterRequestHandler("test/ignores-cancel", async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation deliberately ignored.
            }

            await release.Task.ConfigureAwait(false);
            return EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
        });
        client.Start();
        agent.Start();

        using CancellationTokenSource caller = new();
        Task<JsonElement> call = client.RequestAsync("test/ignores-cancel", null, caller.Token);
        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        await caller.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<AcpCancellationUnconfirmedException>(() => call).ConfigureAwait(false);
        await client.Completion.ConfigureAwait(false);
        release.TrySetResult();
    }

    [Fact]
    public async Task PeerCancelsOurRequest_OutcomeStaysUnknownUntilGraceElapses()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect(
            new AcpRpcOptions { CancelGracePeriod = TimeSpan.FromMilliseconds(150) });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The agent takes the request but never answers, even after receiving our cancel path.
        agent.RegisterRequestHandler("test/orphan", async (_, _) =>
        {
            await release.Task.ConfigureAwait(false);
            return EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
        });
        client.Start();
        agent.Start();

        Task<JsonElement> call = client.RequestAsync("test/orphan", null, test.Token);
        await RpcTestHarness.SendFrameAsync(agentWire, "{\"method\":\"$/cancel_request\",\"params\":{\"requestId\":\"acp-1\"}}").ConfigureAwait(false);

        await Assert.ThrowsAsync<AcpCancellationUnconfirmedException>(() => call).ConfigureAwait(false);
        await client.Completion.ConfigureAwait(false);
        release.TrySetResult();
    }

    [Fact]
    public async Task Request_Timeout_BehavesLikeCancellation()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect(
            new AcpRpcOptions { RequestTimeout = TimeSpan.FromMilliseconds(120), CancelGracePeriod = TimeSpan.FromMilliseconds(100) });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        agent.RegisterRequestHandler("test/ignores-cancel", async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await release.Task.ConfigureAwait(false);
            return EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
        });
        client.Start();
        agent.Start();

        await Assert.ThrowsAsync<AcpCancellationUnconfirmedException>(
            () => client.RequestAsync("test/ignores-cancel", null, test.Token)).ConfigureAwait(false);
        await client.Completion.ConfigureAwait(false);
        release.TrySetResult();
    }

    [Fact]
    public async Task Request_NaturalCompletionWinsRaceOverCancel()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect(
            new AcpRpcOptions { CancelGracePeriod = TimeSpan.FromSeconds(30) });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        agent.RegisterRequestHandler("test/finish-fast", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("value", "done");
            writer.WriteEndObject();
        })));
        client.Start();
        agent.Start();

        using CancellationTokenSource caller = new();
        Task<JsonElement> call = client.RequestAsync("test/finish-fast", null, caller.Token);
        JsonElement answer = await call.ConfigureAwait(false);
        Assert.Equal("done", answer.GetProperty("value").GetString());
        // Cancelling an already-finished call must not disturb anything.
        await caller.CancelAsync().ConfigureAwait(false);
        Assert.True(call.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Request_SlowHandler_DoesNotBlockOtherTraffic()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.RegisterRequestHandler("test/slow", async (_, _) =>
        {
            await release.Task.ConfigureAwait(false);
            return EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
        });
        agent.RegisterRequestHandler("test/fast", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("speed", "fast");
            writer.WriteEndObject();
        })));
        client.Start();
        agent.Start();

        Task<JsonElement> slow = client.RequestAsync("test/slow", null, test.Token);
        // While the slow handler runs, further calls still answer.
        JsonElement fast = await client.RequestAsync("test/fast", null, test.Token).ConfigureAwait(false);
        Assert.Equal("fast", fast.GetProperty("speed").GetString());
        release.TrySetResult();
        await slow.ConfigureAwait(false);
    }
}
