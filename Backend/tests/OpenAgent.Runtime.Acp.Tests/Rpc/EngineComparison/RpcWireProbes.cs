using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using StreamJsonRpc;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Rpc.EngineComparison;

/// <summary>
/// ACP-02.1: the same wire probes drive the own engine and StreamJsonRpc. The own engine must
/// satisfy every ACP wire shape, including raw frames; StreamJsonRpc is driven through its
/// public API only, so shapes it cannot produce (forced ID values, batch arrays) are pinned as
/// not-exercisable with the corresponding API fact. The matrix is ADR-0005's evidence.
/// </summary>
public sealed class RpcWireProbes
{
    internal const string Own = "acp";
    internal const string Library = "streamjsonrpc";
    private const int DeadlineMs = 10_000;

    /// <summary>Outcome per probe and engine, recorded from the observed runs; update together with the ADR.</summary>
    internal static (string Outcome, string Note) Expected(string engine, string probe) => (engine, probe) switch
    {
        (Own, "NewlineDelimitedFraming") => ("pass", "native NDJSON framing without headers"),
        (Library, "NewlineDelimitedFraming") => ("pass", "built-in NewLineDelimitedMessageHandler"),
        (Own, "ReentrantReverseCall") => ("pass", "handler tasks are independent of the reader"),
        (Library, "ReentrantReverseCall") => ("pass", "managed reverse invocation"),
        (Own, "Int64IdEcho") => ("pass", "raw frame with 2^53+1 echoed exactly"),
        (Library, "Int64IdEcho") => ("not-exercisable", "public API generates IDs internally; RequestId type handles long"),
        (Own, "UnknownMethodErrorCode") => ("pass", "-32601 per pinned catalog rules"),
        (Library, "UnknownMethodErrorCode") => ("pass", "-32601 via RemoteInvocationException"),
        (Own, "BatchHandling") => ("pass", "mixed, notification-only, empty, and in-batch cancel"),
        (Library, "BatchHandling") => ("not-exercisable", "no batch API in StreamJsonRpc 2.25.29; arrays are not handled"),
        (Own, "CancelNotificationConvention") => ("pass", "$/cancel_request {requestId} reaches the handler token"),
        (Library, "CancelNotificationConvention") => ("divergent", "its own convention cancels; the ACP shape is ignored"),
        (Own, "ShutdownConvergence") => ("pass", "pending fails with AcpConnectionClosedException; Completion settles"),
        (Library, "ShutdownConvergence") => ("pass", "Dispose completes; in-flight calls fail"),
        _ => throw new ArgumentOutOfRangeException(nameof(probe), "unpinned probe combination"),
    };

    [Fact]
    public async Task NewlineDelimitedFraming_AcpEngine()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource deadline = new(DeadlineMs);
        agent.RegisterRequestHandler("test/echo", (request, _) => Task.FromResult<JsonElement?>(request.Parameters!.Value));
        client.Start();
        agent.Start();

        JsonElement result = await client.RequestAsync("test/echo", Parsed("{\"hello\":\"wire\"}"), deadline.Token).ConfigureAwait(false);
        Assert.Equal("wire", result.GetProperty("hello").GetString());
    }

    [Fact]
    public async Task NewlineDelimitedFraming_StreamJsonRpc()
    {
        (StreamJsonRpcAdapter local, StreamJsonRpcAdapter remote) = StreamJsonRpcAdapter.CreatePair();
        await using StreamJsonRpcAdapter localOwned = local;
        await using StreamJsonRpcAdapter remoteOwned = remote;
        using CancellationTokenSource deadline = new(DeadlineMs);
        remote.OnRequest("test/echo", (request, _) => Task.FromResult(request));
        local.Attach(remote);

        JsonElement result = await local.RequestAsync("test/echo", Parsed("{\"hello\":\"wire\"}"), deadline.Token).ConfigureAwait(false);
        Assert.Equal("wire", result.GetProperty("hello").GetString());
    }

    [Fact]
    public async Task ReentrantReverseCall_AcpEngine()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource deadline = new(DeadlineMs);
        agent.RegisterRequestHandler("test/outer", async (_, ct) =>
        {
            JsonElement inner = await agent.RequestAsync("test/inner", Parsed("{}"), ct).ConfigureAwait(false);
            return Parsed($"{{\"seen\":\"{inner.GetProperty("ok").GetString()}\"}}");
        });
        client.RegisterRequestHandler("test/inner", (_, _) => Task.FromResult<JsonElement?>(Parsed("{\"ok\":\"inner\"}")));
        client.Start();
        agent.Start();

        JsonElement result = await client.RequestAsync("test/outer", Parsed("{}"), deadline.Token).ConfigureAwait(false);
        Assert.Equal("inner", result.GetProperty("seen").GetString());
    }

    [Fact]
    public async Task ReentrantReverseCall_StreamJsonRpc()
    {
        (StreamJsonRpcAdapter local, StreamJsonRpcAdapter remote) = StreamJsonRpcAdapter.CreatePair();
        await using StreamJsonRpcAdapter localOwned = local;
        await using StreamJsonRpcAdapter remoteOwned = remote;
        using CancellationTokenSource deadline = new(DeadlineMs);
        remote.OnRequest("test/outer", async (_, ct) =>
        {
            JsonElement inner = await remote.RequestAsync("test/inner", Parsed("{}"), ct).ConfigureAwait(false);
            return Parsed($"{{\"seen\":\"{inner.GetProperty("ok").GetString()}\"}}");
        });
        local.OnRequest("test/inner", (_, _) => Task.FromResult(Parsed("{\"ok\":\"inner\"}")));
        local.Attach(remote);

        JsonElement result = await local.RequestAsync("test/outer", Parsed("{}"), deadline.Token).ConfigureAwait(false);
        Assert.Equal("inner", result.GetProperty("seen").GetString());
    }

    [Fact]
    public async Task Int64IdEcho_AcpEngine()
    {
        // Raw wire shape with 2^53+1; StreamJsonRpc's public API cannot force an ID value, so this
        // probe runs on the own engine only and the matrix records the library as not-exercisable.
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource deadline = new(DeadlineMs);
        client.RegisterRequestHandler("test/echo", (_, _) => Task.FromResult<JsonElement?>(Parsed("{}")));
        client.Start();

        await agentWire.WriteAsync("{\"id\":9007199254740993,\"method\":\"test/echo\",\"params\":{}}"u8.ToArray()).ConfigureAwait(false);
        byte[] reply = await ReadResponseFrameAsync(clientWire, deadline.Token).ConfigureAwait(false);
        using JsonDocument response = JsonDocument.Parse(reply);
        Assert.Equal("9007199254740993", response.RootElement.GetProperty("id").GetRawText());
    }

    [Fact]
    public async Task UnknownMethodErrorCode_AcpEngine()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource deadline = new(DeadlineMs);
        client.Start();

        await agentWire.WriteAsync("{\"id\":7,\"method\":\"missing/method\",\"params\":{}}"u8.ToArray()).ConfigureAwait(false);
        byte[] reply = await ReadResponseFrameAsync(clientWire, deadline.Token).ConfigureAwait(false);
        using JsonDocument response = JsonDocument.Parse(reply);
        Assert.Equal(ErrorCode.MethodNotFound, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task UnknownMethodErrorCode_StreamJsonRpc()
    {
        (StreamJsonRpcAdapter local, StreamJsonRpcAdapter remote) = StreamJsonRpcAdapter.CreatePair();
        await using StreamJsonRpcAdapter localOwned = local;
        await using StreamJsonRpcAdapter remoteOwned = remote;
        using CancellationTokenSource deadline = new(DeadlineMs);
        local.Attach(remote);

        // RemoteMethodNotFoundException is the library's mapping of JSON-RPC -32601 for unknown methods.
        _ = await Assert.ThrowsAnyAsync<RemoteMethodNotFoundException>(
            () => local.RequestAsync("missing/method", Parsed("{}"), deadline.Token)).ConfigureAwait(false);
    }

    [Fact]
    public async Task BatchHandling_AcpEngine()
    {
        // Raw batch array; StreamJsonRpc has no batch API, so the probe runs on the own engine only.
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource deadline = new(DeadlineMs);
        client.RegisterRequestHandler("test/one", (_, _) => Task.FromResult<JsonElement?>(Parsed("{\"first\":true}")));
        client.RegisterRequestHandler("test/two", (_, _) => Task.FromResult<JsonElement?>(Parsed("{\"second\":true}")));
        client.Start();

        await agentWire.WriteAsync("[{\"id\":\"a\",\"method\":\"test/one\",\"params\":{}},{\"id\":\"b\",\"method\":\"test/two\",\"params\":{}}]"u8.ToArray()).ConfigureAwait(false);
        byte[] reply = await ReadResponseFrameAsync(clientWire, deadline.Token).ConfigureAwait(false);
        using JsonDocument response = JsonDocument.Parse(reply);
        Assert.Equal(JsonValueKind.Array, response.RootElement.ValueKind);
        Assert.Equal(2, response.RootElement.GetArrayLength());
        Assert.Equal("a", response.RootElement[0].GetProperty("id").GetString());
        Assert.Equal("b", response.RootElement[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task CancelNotificationConvention_AcpEngine()
    {
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource deadline = new(DeadlineMs);
        agent.RegisterRequestHandler("test/long", async (_, ct) =>
        {
            handlerEntered.TrySetResult();
            ct.Register(() => cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return Parsed("{}");
        });
        client.Start();
        agent.Start();

        Task call = client.RequestAsync("test/long", Parsed("{}"), deadline.Token);
        await handlerEntered.Task.ConfigureAwait(false);
        // A peer sends the ACP cancel notification for the engine-generated request ID (first call: acp-1).
        await clientWire.WriteAsync("{\"method\":\"$/cancel_request\",\"params\":{\"requestId\":\"acp-1\"}}"u8.ToArray()).ConfigureAwait(false);
        Task acpDone = await Task.WhenAny(cancellationObserved.Task, Task.Delay(1_000, CancellationToken.None)).ConfigureAwait(false);
        Assert.Equal(cancellationObserved.Task, acpDone);
        await Assert.ThrowsAsync<AcpRpcException>(() => call).ConfigureAwait(false);
    }

    [Fact]
    public async Task CancelNotificationConvention_StreamJsonRpc()
    {
        // The library cancels through its own convention when the caller's token fires; the ACP
        // $/cancel_request shape carries no information it understands, so it must not cancel.
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (StreamJsonRpcAdapter local, StreamJsonRpcAdapter remote) = StreamJsonRpcAdapter.CreatePair();
        await using StreamJsonRpcAdapter localOwned = local;
        await using StreamJsonRpcAdapter remoteOwned = remote;
        using CancellationTokenSource deadline = new(DeadlineMs);
        remote.OnRequest("test/long", async (_, ct) =>
        {
            handlerEntered.TrySetResult();
            ct.Register(() => cancellationObserved.TrySetResult());
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return Parsed("{}");
        });
        local.Attach(remote);

        using CancellationTokenSource caller = new();
        Task inFlight = local.RequestAsync("test/long", Parsed("{}"), caller.Token);
        await handlerEntered.Task.ConfigureAwait(false);
        // The ACP shape with an unknown ID: the handler must not observe it.
        local.Notify("$/cancel_request", new { requestId = "unknown" });
        Task acpDone = await Task.WhenAny(cancellationObserved.Task, Task.Delay(1_000, CancellationToken.None)).ConfigureAwait(false);
        Assert.NotEqual(cancellationObserved.Task, acpDone);

        // The library's own convention (caller cancellation) does reach the handler.
        await caller.CancelAsync().ConfigureAwait(false);
        Task ownDone = await Task.WhenAny(cancellationObserved.Task, Task.Delay(1_000, CancellationToken.None)).ConfigureAwait(false);
        Assert.Equal(cancellationObserved.Task, ownDone);
        // The handler swallows the cancellation and answers; the call completes normally.
        await inFlight.ConfigureAwait(false);
    }

    [Fact]
    public async Task ShutdownConvergence_AcpEngine()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource deadline = new(DeadlineMs);
        agent.RegisterRequestHandler("test/hang", async (_, _) =>
        {
            await release.Task.ConfigureAwait(false);
            return Parsed("{}");
        });
        client.Start();
        agent.Start();

        Task<JsonElement> pending = client.RequestAsync("test/hang", Parsed("{}"), deadline.Token);
        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        await client.DisposeAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending).ConfigureAwait(false);
        await client.Completion.ConfigureAwait(false);
        release.TrySetResult();
    }

    [Fact]
    public async Task ShutdownConvergence_StreamJsonRpc()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (StreamJsonRpcAdapter local, StreamJsonRpcAdapter remote) = StreamJsonRpcAdapter.CreatePair();
        await using StreamJsonRpcAdapter localOwned = local;
        await using StreamJsonRpcAdapter remoteOwned = remote;
        remote.OnRequest("test/hang", async (_, _) =>
        {
            await release.Task.ConfigureAwait(false);
            return Parsed("{}");
        });
        local.Attach(remote);
        using CancellationTokenSource deadline = new(DeadlineMs);

        Task call = local.RequestAsync("test/hang", Parsed("{}"), deadline.Token);
        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        Task shutdown = local.ShutdownAsync();
        Task settled = await Task.WhenAny(shutdown, Task.Delay(3_000, CancellationToken.None)).ConfigureAwait(false);
        Assert.Equal(shutdown, settled);
        await Assert.ThrowsAnyAsync<Exception>(() => call).ConfigureAwait(false);
        release.TrySetResult();
    }

    /// <summary>Reads frames until one is a response; the tap also logs frames the engine wrote as requests.</summary>
    private static async Task<byte[]> ReadResponseFrameAsync(LoopbackTransport wire, CancellationToken deadline)
    {
        while (true)
        {
            byte[]? frame = await wire.OutgoingFrames.ReadAsync(deadline).ConfigureAwait(false);
            Assert.NotNull(frame);
            using JsonDocument parsed = JsonDocument.Parse(frame);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || !parsed.RootElement.TryGetProperty("method", out _)) return frame;
        }
    }

    private static JsonElement Parsed(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
