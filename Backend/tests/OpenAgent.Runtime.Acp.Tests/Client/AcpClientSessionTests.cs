using System.Text.Json;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Client;

/// <summary>
/// Client role behavior over raw-wire loopback: initialize gating, version negotiation,
/// early-update binding, prompt ordering, cancellation, default-deny permission, and shutdown.
/// </summary>
public sealed class AcpClientSessionTests
{
    private static (AcpClientConnection Client, LoopbackTransport ClientWire, LoopbackTransport AgentWire) CreatePair(AcpClientOptions? options = null)
    {
        (LoopbackTransport clientWire, LoopbackTransport agentWire) = LoopbackTransport.CreatePair();
        AcpClientConnection client = new(clientWire, options);
        return (client, clientWire, agentWire);
    }

    /// <summary>Starts the client and completes initialize against a raw agent that answers v1 with image support.</summary>
    private static async Task<RawPeer> InitializeAsync(AcpClientConnection client, LoopbackTransport agentWire, CancellationToken deadline, string capabilities = """
        {"promptCapabilities":{"image":true}}
        """)
    {
        client.Start();
        RawPeer peer = new(agentWire);
        Task<InitializeResponse> initializing = client.InitializeAsync(deadline);
        using JsonDocument request = await peer.ReceiveRequestAsync("initialize", deadline);
        await peer.AnswerAsync(request, $$"""{"protocolVersion":1,"agentCapabilities":{{capabilities}}}""");
        await initializing;
        return peer;
    }

    /// <summary>Creates one session against the raw agent, which answers with the given id.</summary>
    private static async Task<AcpSession> NewSessionAsync(AcpClientConnection client, RawPeer peer, string sessionId, CancellationToken deadline)
    {
        Task<AcpSession> creating = client.NewSessionAsync("/tmp", deadline);
        using JsonDocument request = await peer.ReceiveRequestAsync("session/new", deadline);
        await peer.AnswerAsync(request, $$"""{"sessionId":"{{sessionId}}"}""");
        return await creating;
    }

    [Fact]
    public async Task InitializeAsync_AgentAnswersV1_StoresNegotiation()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        Assert.True(client.IsReady);
        Assert.Equal(1, client.Negotiated!.ProtocolVersion);
        Assert.True(client.Negotiated!.AgentCapabilities!.Value.PromptCapabilitiesOrDefault.ImageOrDefault);
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task InitializeAsync_SecondCall_Fails()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync(deadline.Token));
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task NewSessionAsync_BeforeInitialize_FailsLocally()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, _) = CreatePair();
        await using AcpClientConnection lifetime = client;
        client.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NewSessionAsync("/tmp", deadline.Token));
        Drain(clientWire);
        await EngineTestUtil.AssertNoFrameAsync(clientWire, deadline.Token);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_AgentAnswersOtherVersion_ClientThrowsCapabilityAndCloses()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        client.Start();
        RawPeer peer = new(agentWire);
        Task initialize = client.InitializeAsync(deadline.Token);
        using (JsonDocument request = await peer.ReceiveRequestAsync("initialize", deadline.Token))
        {
            await peer.AnswerAsync(request, """{"protocolVersion":2}""");
        }

        AcpCapabilityException failure = await Assert.ThrowsAsync<AcpCapabilityException>(() => initialize);
        Assert.Contains("version 2", failure.Message);
        await client.Completion;
        Assert.False(client.IsReady);
    }

    [Fact]
    public async Task NewSessionAsync_RelativeCwd_FailsBeforeWire()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        await Assert.ThrowsAsync<ArgumentException>(() => client.NewSessionAsync("relative/path", deadline.Token));
        Drain(clientWire);
        await EngineTestUtil.AssertNoFrameAsync(clientWire, deadline.Token);
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task NewSessionAsync_UpdatesBeforeResponse_AreBoundToTheSession()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        Task<AcpSession> creating = client.NewSessionAsync("/tmp", deadline.Token);
        using (JsonDocument request = await peer.ReceiveRequestAsync("session/new", deadline.Token))
        {
            // The agent answers late: a session update first, then the response, in wire order.
            await peer.WriteAsync("""
                {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess-early","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"早到"}}}}
                """);
            await peer.AnswerAsync(request, """{"sessionId":"sess-early"}""");
        }

        AcpSession session = await creating;
        Assert.Equal("sess-early", session.SessionId);
        List<SessionNotification> updates = await CollectAsync(session, 1, deadline.Token);
        Assert.Equal("早到", TextOf(updates[0]));
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task NewSessionAsync_ManyEarlyUpdates_KeepWireOrderAcrossBinding()
    {
        // BindSession once enqueued its pre-response snapshot outside the bind gate, so the
        // dispatch loop could slip a still-in-flight update into the middle of the handoff and
        // silently reorder the stream (review F1). Many rounds with a wide early window and
        // post-binding traffic pin the full wire order on every round.
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);

        const int earlyCount = 80;
        const int afterCount = 8;
        for (int round = 0; round < 25; round++)
        {
            string sessionId = $"sess-order-{round}";
            Task<AcpSession> creating = client.NewSessionAsync("/tmp", deadline.Token);
            using (JsonDocument request = await peer.ReceiveRequestAsync("session/new", deadline.Token))
            {
                for (int index = 0; index < earlyCount; index++)
                {
                    await peer.WriteAsync(UpdateFrame(sessionId, $"早{index:000}"));
                }

                await peer.AnswerAsync(request, $$"""{"sessionId":"{{sessionId}}"}""");
                // The agent keeps streaming while the binding hands the early window over.
                for (int index = 0; index < afterCount; index++)
                {
                    await peer.WriteAsync(UpdateFrame(sessionId, $"后{index:000}"));
                }
            }

            AcpSession session = await creating;
            List<SessionNotification> updates = await CollectAsync(session, earlyCount + afterCount, deadline.Token);
            string[] expected =
            [
                .. Enumerable.Range(0, earlyCount).Select(index => $"早{index:000}"),
                .. Enumerable.Range(0, afterCount).Select(index => $"后{index:000}")
            ];
            Assert.Equal(expected, updates.Select(TextOf).ToArray());
        }

        clientWire.SimulateEof();
    }

    [Fact]
    public async Task PromptAsync_UpdatesPrecedeResponse_AllEnqueuedBeforeCompletion()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);

        List<SessionNotification> updates = [];
        Task consumer = CollectForeverAsync(session, updates);
        AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText("hi"))], deadline.Token);
        using (JsonDocument request = await peer.ReceiveRequestAsync("session/prompt", deadline.Token))
        {
            for (int index = 0; index < 5; index++)
            {
                await peer.WriteAsync(UpdateFrame("sess-1", $"块{index}"));
            }

            await peer.AnswerAsync(request, """{"stopReason":"end_turn"}""");
        }

        PromptResponse final = await turn.Completion;
        Assert.Equal(StopReason.EndTurn, final.StopReason);
        // Completion promises the updates are enqueued; a bounded consumer drain proves it.
        await EngineTestUtil.WaitForAsync(() =>
        {
            lock (updates) return updates.Count == 5;
        }, deadline.Token);
        lock (updates)
        {
            string[] expected = ["块0", "块1", "块2", "块3", "块4"];
            Assert.Equal(expected, updates.Select(TextOf).ToArray());
        }
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task StartPromptAsync_SecondConcurrentTurn_ThrowsSessionBusyImmediately()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);
        AcpPromptTurn first = await session.StartPromptAsync([new ContentBlock(new ContentBlockText("one"))], deadline.Token);
        SessionBusyException busy = await Assert.ThrowsAsync<SessionBusyException>(
            () => session.StartPromptAsync([new ContentBlock(new ContentBlockText("two"))], deadline.Token));
        Assert.Equal(session.SessionId, busy.SessionId);
        // The busy turn never touched the wire; the first turn still completes normally.
        using JsonDocument prompt = await peer.ReceiveRequestAsync("session/prompt", deadline.Token);
        await peer.AnswerAsync(prompt, """{"stopReason":"end_turn"}""");
        PromptResponse final = await first.Completion;
        Assert.Equal(StopReason.EndTurn, final.StopReason);
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task StartPromptAsync_AfterTurnFinished_SessionAcceptsNextTurn()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);
        for (int index = 0; index < 2; index++)
        {
            AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText("t"))], deadline.Token);
            using JsonDocument request = await peer.ReceiveRequestAsync("session/prompt", deadline.Token);
            await peer.AnswerAsync(request, """{"stopReason":"end_turn"}""");
            PromptResponse final = await turn.Completion;
            Assert.Equal(StopReason.EndTurn, final.StopReason);
        }

        clientWire.SimulateEof();
    }

    [Fact]
    public async Task CancelAsync_TrailingUpdatesStayOrderedBeforeConfirmedResponse()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);

        List<SessionNotification> updates = [];
        Task consumer = CollectForeverAsync(session, updates);
        AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText("long"))], deadline.Token);
        using (JsonDocument request = await peer.ReceiveRequestAsync("session/prompt", deadline.Token))
        {
            await turn.CancelAsync(deadline.Token);
            using JsonDocument cancel = await peer.ReceiveNotificationAsync("session/cancel", deadline.Token);
            Assert.Equal("sess-1", cancel.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
            // The agent keeps streaming after the cancel; the final response confirms it.
            await peer.WriteAsync("""
                {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess-1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"尾部"}}}}
                """);
            await peer.AnswerAsync(request, """{"stopReason":"cancelled"}""");
        }

        PromptResponse final = await turn.Completion;
        Assert.Equal(StopReason.Cancelled, final.StopReason);
        await EngineTestUtil.WaitForAsync(() =>
        {
            lock (updates) return updates.Count == 1;
        }, deadline.Token);
        lock (updates) Assert.Equal("尾部", TextOf(updates[0]));
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task CancelAndWaitAsync_NaturalCompletionWinsTheRace()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);

        AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText("quick"))], deadline.Token);
        Task<PromptResponse> waiting = turn.CancelAndWaitAsync(deadline.Token);
        using (JsonDocument request = await peer.ReceiveRequestAsync("session/prompt", deadline.Token))
        {
            // The agent ignores the cancel and completes naturally first.
            await peer.AnswerAsync(request, """{"stopReason":"end_turn"}""");
        }

        PromptResponse final = await waiting;
        Assert.Equal(StopReason.EndTurn, final.StopReason);
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task RequestPermission_DefaultAnswerIsCancelledWithoutAutoApproval()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"perm-1","method":"session/request_permission","params":{"sessionId":"sess-1","toolCall":{"toolCallId":"t1","title":"run","kind":"execute","status":"pending"},"options":[{"optionId":"allow","name":"Allow","kind":"allow_once"}]}}
            """);
        using JsonDocument answer = await peer.ReceiveResponseAsync("perm-1", deadline.Token);
        Assert.Equal("cancelled", answer.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task UnsupportedReverseRequest_AnsweredMethodNotFound()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"fs-1","method":"fs/read_text_file","params":{"sessionId":"sess-1","path":"/tmp/x"}}
            """);
        using JsonDocument answer = await peer.ReceiveResponseAsync("fs-1", deadline.Token);
        Assert.Equal(ErrorCode.MethodNotFound, answer.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task StartPromptAsync_UnnegotiatedContent_ThrowsCapabilityBeforeWire()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token, capabilities: "{}");
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);
        AcpCapabilityException failure = await Assert.ThrowsAsync<AcpCapabilityException>(
            () => session.StartPromptAsync([new ContentBlock(new ContentBlockImage("data:image/png;base64,AAAA", "image/png"))], deadline.Token));
        Assert.Contains("image", failure.Message);
        Drain(clientWire);
        await EngineTestUtil.AssertNoFrameAsync(clientWire, deadline.Token);
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task ConnectionEof_FailsPendingTurnFreesSessionStreamAndConverges()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);
        List<SessionNotification> updates = [];
        Task consumer = CollectForeverAsync(session, updates);
        AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText("bye"))], deadline.Token);
        _ = await peer.ReceiveRequestAsync("session/prompt", deadline.Token);

        clientWire.SimulateEof();
        AcpConnectionClosedException failure = await Assert.ThrowsAsync<AcpConnectionClosedException>(() => turn.Completion);
        Assert.NotEmpty(failure.Reason);
        await client.Completion;
        // The update stream ends instead of hanging the consumer.
        try
        {
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
        Assert.Equal(0, client.TrackedTaskCount);
    }

    [Fact]
    public async Task NewSessionAsync_AgentAnswersBlankSessionId_FailsLocally()
    {
        // The wire schema cannot express a minimum length, so the client rejects a blank id
        // before binding instead of creating a session no update could route to.
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        Task<AcpSession> creating = client.NewSessionAsync("/tmp", deadline.Token);
        using (JsonDocument request = await peer.ReceiveRequestAsync("session/new", deadline.Token))
        {
            await peer.AnswerAsync(request, """{"sessionId":""}""");
        }

        AcpCapabilityException failure = await Assert.ThrowsAsync<AcpCapabilityException>(() => creating);
        Assert.Contains("blank session id", failure.Message);
        Assert.True(client.IsReady); // A one-off protocol violation fails the call, not the link.
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task ReadUpdatesAsync_SecondConcurrentConsumer_StillFails()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);
        IAsyncEnumerable<SessionNotification> first = session.ReadUpdatesAsync(deadline.Token);
        InvalidOperationException busy = Assert.Throws<InvalidOperationException>(() => session.ReadUpdatesAsync(deadline.Token));
        Assert.Contains("exactly one consumer", busy.Message);
        await DisposeAsync(first);
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task ReadUpdatesAsync_SecondConsumerAfterFirstEnded_ContinuesFromNextUpdate()
    {
        // The consumer slot used to stay taken forever, so even a sequential second consumer
        // failed; it frees when an enumeration ends (review F5).
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession session = await NewSessionAsync(client, peer, "sess-1", deadline.Token);

        await peer.WriteAsync(UpdateFrame("sess-1", "一"));
        await peer.WriteAsync(UpdateFrame("sess-1", "二"));
        List<SessionNotification> firstPass = await CollectAsync(session, 2, deadline.Token);
        Assert.Equal(new[] { "一", "二" }, firstPass.Select(TextOf).ToArray());

        // The first enumeration ended; a later consumer continues from the next queued update.
        await peer.WriteAsync(UpdateFrame("sess-1", "三"));
        List<SessionNotification> secondPass = await CollectAsync(session, 1, deadline.Token);
        Assert.Equal("三", TextOf(secondPass[0]));
        clientWire.SimulateEof();
    }

    [Fact]
    public async Task ConnectionEof_AfterOneSessionDisposed_RemainingSessionStreamStillEnds()
    {
        // OnConnectionClosed once cancelled a disposed session's token and threw through the
        // connection fan-out, so every later session missed the close and its stream hung
        // (review F3). Disposing the first of two sessions must not strand the second.
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        await using AcpClientConnection lifetime = client;
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        AcpSession first = await NewSessionAsync(client, peer, "sess-a", deadline.Token);
        AcpSession second = await NewSessionAsync(client, peer, "sess-b", deadline.Token);
        List<SessionNotification> updates = [];
        Task consumer = CollectForeverAsync(second, updates);

        await first.DisposeAsync();
        clientWire.SimulateEof();
        await client.Completion;
        // The remaining session's stream ends instead of hanging the consumer.
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, client.TrackedTaskCount);
    }

    [Fact]
    public async Task DisposeAsync_TwiceConvergesAndStaysIdempotent()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpClientConnection client, LoopbackTransport clientWire, LoopbackTransport agentWire) = CreatePair();
        RawPeer peer = await InitializeAsync(client, agentWire, deadline.Token);
        ValueTask first = client.DisposeAsync();
        await client.DisposeAsync();
        await first;
        Assert.Equal(0, client.TrackedTaskCount);
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(15));

    /// <summary>Emits frames the handshake left on the tap, so silence assertions start clean.</summary>
    private static void Drain(LoopbackTransport wire)
    {
        while (wire.OutgoingFrames.TryRead(out _)) { }
    }

    /// <summary>Builds one session/update notification frame for the raw agent side.</summary>
    private static string UpdateFrame(string sessionId, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"" + sessionId
        + "\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":"
        + AcpJson.Serialize(text) + "}}}}";

    private static async Task<List<SessionNotification>> CollectAsync(AcpSession session, int count, CancellationToken deadline)
    {
        List<SessionNotification> updates = [];
        await foreach (SessionNotification update in session.ReadUpdatesAsync(deadline))
        {
            updates.Add(update);
            if (updates.Count >= count) break;
        }

        return updates;
    }

    /// <summary>Releases an abandoned consumer without enumerating it.</summary>
    private static async Task DisposeAsync(IAsyncEnumerable<SessionNotification> enumerable)
    {
        IAsyncEnumerator<SessionNotification> enumerator = enumerable.GetAsyncEnumerator();
        await enumerator.DisposeAsync();
    }

    private static Task CollectForeverAsync(AcpSession session, List<SessionNotification> updates) => Task.Run(async () =>
    {
        try
        {
            await foreach (SessionNotification update in session.ReadUpdatesAsync())
            {
                lock (updates) updates.Add(update);
            }
        }
        catch
        {
            // The test disposes the connection while the consumer waits; collected counts stay readable.
        }
    });

    private static string TextOf(SessionNotification update) => update.Update.AsAgentMessageChunk().Content.AsText().Text;
}
