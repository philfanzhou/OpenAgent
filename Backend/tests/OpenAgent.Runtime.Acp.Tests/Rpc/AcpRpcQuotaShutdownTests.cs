using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>Quotas and lifecycle convergence (V12/V18): pending and handler quotas, notification overflow, unified close.</summary>
public sealed class AcpRpcQuotaShutdownTests
{
    [Fact]
    public async Task Request_PendingQuotaExhausted_FailsLocallyWithoutBlocking()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect(
            new AcpRpcOptions { MaxPendingRequests = 3 });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The agent takes requests but answers nothing until the test releases.
        agent.RegisterRequestHandler("test/hang", async (_, _) =>
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

        try
        {
            Task first = client.RequestAsync("test/hang", null, test.Token);
            Task second = client.RequestAsync("test/hang", null, test.Token);
            Task third = client.RequestAsync("test/hang", null, test.Token);
            // Three in-flight calls fill MaxPendingRequests=3; the fourth fails locally before any frame is written.
            await Assert.ThrowsAsync<AcpQuotaExceededException>(
                () => client.RequestAsync("test/hang", null, test.Token)).ConfigureAwait(false);

            // Exhausting the quota never blocks responses, cancels, or shutdown.
            release.TrySetResult();
            await Task.WhenAll(first, second, third).ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Request_HandlerTaskQuotaExhausted_AnsweredInternalError()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect(
            agentOptions: new AcpRpcOptions { MaxConcurrentHandlers = 1 });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.RegisterRequestHandler("test/slow", async (_, _) =>
        {
            firstStarted.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
        });
        agent.RegisterRequestHandler("test/second", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        })));
        client.Start();
        agent.Start();

        try
        {
            await RpcTestHarness.SendFrameAsync(clientWire, "{\"id\":\"one\",\"method\":\"test/slow\",\"params\":{}}").ConfigureAwait(false);
            await firstStarted.Task.ConfigureAwait(false);
            await RpcTestHarness.SendFrameAsync(clientWire, "{\"id\":\"two\",\"method\":\"test/second\",\"params\":{}}").ConfigureAwait(false);

            JsonDocument? response = await RpcTestHarness.ReceiveFrameAsync(agentWire, test.Token).ConfigureAwait(false);
            Assert.NotNull(response);
            Assert.Equal("two", response!.RootElement.GetProperty("id").GetString());
            Assert.Equal(ErrorCode.InternalError, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Notification_QueueOverflow_FaultsConnection()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect(
            new AcpRpcOptions { MaxQueuedNotifications = 1 });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;

        TaskCompletionSource firstSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The handler honors its lifetime token, so the overflow shutdown converges without external release.
        client.RegisterNotificationHandler("test/note", async (_, ct) =>
        {
            firstSeen.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        });
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, "{\"method\":\"test/note\",\"params\":{}}").ConfigureAwait(false);
        await firstSeen.Task.ConfigureAwait(false);
        // The handler is stalled; the next two notifications exceed the queue. The queue only
        // drains again after shutdown cancels the handler, so the overflow is deterministic.
        await RpcTestHarness.SendFrameAsync(agentWire, "{\"method\":\"test/note\",\"params\":{}}").ConfigureAwait(false);
        await RpcTestHarness.SendFrameAsync(agentWire, "{\"method\":\"test/note\",\"params\":{}}").ConfigureAwait(false);

        // Slow consumption faults the connection with the quota error instead of dropping frames
        // or blocking the reader forever.
        await Assert.ThrowsAnyAsync<AcpQuotaExceededException>(() => client.Completion).ConfigureAwait(false);
    }

    [Fact]
    public async Task Notification_HandlerFault_ConnectionServesOn()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        List<AcpRpcDiagnosticEvent> diagnostics = [];

        client.RegisterNotificationHandler("test/throws", (_, _) => throw new InvalidOperationException("bug"));
        agent.RegisterRequestHandler("test/echo", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("ok", "yes");
            writer.WriteEndObject();
        })));
        client.Start();
        agent.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, "{\"method\":\"test/throws\"}").ConfigureAwait(false);
        JsonElement result = await client.RequestAsync("test/echo", null, test.Token).ConfigureAwait(false);
        Assert.Equal("yes", result.GetProperty("ok").GetString());
    }

    [Fact]
    public async Task Dispose_FailsPendingAndIsIdempotent()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        agent.RegisterRequestHandler("test/hang", async (_, _) =>
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

        Task<JsonElement> pending = client.RequestAsync("test/hang", null, test.Token);
        try
        {
            // The frame must be on the wire before disposal: an unwritten frame is a documented
            // pre-write cancellation, and this test pins the pending-failure path.
            byte[]? written = await clientWire.OutgoingFrames.ReadAsync(test.Token).ConfigureAwait(false);
            Assert.NotNull(written);
            await ValueTaskExtensionsDisposeTwice(client).ConfigureAwait(false);

            await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending).ConfigureAwait(false);
            await client.Completion.ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static async Task ValueTaskExtensionsDisposeTwice(AcpRpcConnection connection)
    {
        ValueTask first = connection.DisposeAsync();
        ValueTask second = connection.DisposeAsync();
        await first.ConfigureAwait(false);
        await second.ConfigureAwait(false);
    }

    [Fact]
    public async Task Eof_FailsPendingAndBothSidesConverge()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        agent.RegisterRequestHandler("test/hang", async (_, _) =>
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

        Task<JsonElement> pending = client.RequestAsync("test/hang", null, test.Token);
        try
        {
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            clientWire.SimulateEof();

            await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending).ConfigureAwait(false);
            await client.Completion.ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
        }

        // The agent faults when its late response hits the closed peer transport.
        await Assert.ThrowsAnyAsync<Exception>(() => agent.Completion).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransportWriteFault_ConnectionFaultsAndPendingObserves()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.RegisterRequestHandler("test/reply", async (_, _) =>
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

        Task<JsonElement> pending;
        try
        {
            pending = client.RequestAsync("test/reply", null, test.Token);
            clientWire.Fault(new IOException("wire cut"));
        }
        finally
        {
            release.TrySetResult();
        }

        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending).ConfigureAwait(false);
        // A terminal transport fault is preserved on Completion.
        await Assert.ThrowsAnyAsync<Exception>(() => client.Completion).ConfigureAwait(false);
    }
}
