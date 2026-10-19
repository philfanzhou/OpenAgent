using System.Text;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Agent;

/// <summary>
/// Agent host behavior against a raw client: initialize gating, capability advertisement,
/// session limits, single-prompt busy rejection, cancellation, and EOF shutdown.
/// </summary>
public sealed class AcpAgentHostTests
{
    [Fact]
    public async Task InitializeAsync_AnswersV1WithoutUnearnedCapabilities()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"i1","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}
            """);
        using JsonDocument answer = await peer.ReceiveResponseAsync("i1", deadline.Token);
        JsonElement result = answer.RootElement.GetProperty("result");
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(EchoAgentDefinition.Name, result.GetProperty("agentInfo").GetProperty("name").GetString());
        // The echo definition enables no optional prompt capability, so none is advertised.
        Assert.False(result.TryGetProperty("agentCapabilities", out _));
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task InitializeAsync_DeclaredPromptCapability_IsAdvertised()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero).WithPromptCapability("image"), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"i1","method":"initialize","params":{"protocolVersion":1}}
            """);
        using JsonDocument answer = await peer.ReceiveResponseAsync("i1", deadline.Token);
        Assert.True(answer.RootElement.GetProperty("result").GetProperty("agentCapabilities").GetProperty("promptCapabilities").GetProperty("image").GetBoolean());
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task InitializeAsync_SecondInitialize_AnswersInternalError()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        await InitializeAsync(peer, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"i2","method":"initialize","params":{"protocolVersion":1}}
            """);
        using JsonDocument again = await peer.ReceiveResponseAsync("i2", deadline.Token);
        Assert.Equal(ErrorCode.InternalError, again.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task SessionNewAsync_BeforeInitialize_AnswersInternalError()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"n1","method":"session/new","params":{"cwd":"/tmp","mcpServers":[]}}
            """);
        using JsonDocument answer = await peer.ReceiveResponseAsync("n1", deadline.Token);
        Assert.Equal(ErrorCode.InternalError, answer.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("not initialized", answer.RootElement.GetProperty("error").GetProperty("message").GetString());
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task PromptAsync_EchoesGraphemesInOrderAndEndsTurn()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        string sessionId = await NewSessionAsync(peer, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"p1","method":"session/prompt","params":{"sessionId":"SESSION","prompt":[{"type":"text","text":"你好🥐"}]}}
            """.Replace("SESSION", sessionId));

        // Updates stream in wire order and reassemble exactly; the final response closes the turn.
        StringBuilder echoed = new();
        while (true)
        {
            using JsonDocument frame = await peer.ReceiveAsync(deadline.Token);
            if (frame.RootElement.TryGetProperty("method", out JsonElement method) && method.GetString() == "session/update")
            {
                echoed.Append(frame.RootElement.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString());
                continue;
            }

            Assert.Equal("p1", frame.RootElement.GetProperty("id").GetString());
            Assert.Equal(StopReason.EndTurn, frame.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
            break;
        }

        Assert.Equal("Echo: 你好🥐", echoed.ToString());
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task PromptAsync_UnknownSession_AnswersResourceNotFound()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        await InitializeAsync(peer, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"p1","method":"session/prompt","params":{"sessionId":"missing","prompt":[{"type":"text","text":"hi"}]}}
            """);
        using JsonDocument answer = await peer.ReceiveResponseAsync("p1", deadline.Token);
        Assert.Equal(ErrorCode.ResourceNotFound, answer.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task PromptAsync_SecondConcurrentTurn_AnswersSessionBusyWithNamespacedData()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(Timeout.InfiniteTimeSpan), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        string sessionId = await NewSessionAsync(peer, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"p1","method":"session/prompt","params":{"sessionId":"SESSION","prompt":[{"type":"text","text":"first"}]}}
            """.Replace("SESSION", sessionId));
        // The first prompt parks on its cancel window; the concurrent second prompt must fail fast.
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"p2","method":"session/prompt","params":{"sessionId":"SESSION","prompt":[{"type":"text","text":"second"}]}}
            """.Replace("SESSION", sessionId));
        using (JsonDocument busy = await peer.ReceiveResponseAsync("p2", deadline.Token))
        {
            Assert.Equal(AcpAgentErrorCode.SessionBusy, busy.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            JsonElement data = busy.RootElement.GetProperty("error").GetProperty("data");
            Assert.Equal("singlePromptTurn", data.GetProperty(AcpAgentErrorCode.DataNamespace).GetProperty("limit").GetString());
        }

        // Cancelling ends the parked first turn with the confirmed stop reason.
        await peer.WriteAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/cancel\",\"params\":{\"sessionId\":\"" + sessionId + "\"}}");
        while (true)
        {
            using JsonDocument frame = await peer.ReceiveAsync(deadline.Token);
            if (frame.RootElement.TryGetProperty("id", out JsonElement id) && id.GetString() == "p1")
            {
                Assert.Equal(StopReason.Cancelled, frame.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
                break;
            }
        }

        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task PromptAsync_UnnegotiatedContent_AnswersInvalidParams()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        string sessionId = await NewSessionAsync(peer, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"p1","method":"session/prompt","params":{"sessionId":"SESSION","prompt":[{"type":"image","data":"AAAA","mimeType":"image/png"}]}}
            """.Replace("SESSION", sessionId));
        using JsonDocument answer = await peer.ReceiveResponseAsync("p1", deadline.Token);
        Assert.Equal(ErrorCode.InvalidParams, answer.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("image", answer.RootElement.GetProperty("error").GetProperty("message").GetString());
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task NewSessionAsync_HandlerThrows_SessionIsNotTracked()
    {
        using CancellationTokenSource deadline = Deadline();
        AcpAgentBuilder builder = new AcpAgentBuilder()
            .HandlePrompt((_, _) => Task.FromResult(StopReason.EndTurn))
            .HandleNewSession((_, _) => throw new AcpRpcException(new Error(ErrorCode.InvalidParams, "rejected cwd")));
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(builder, deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        await InitializeAsync(peer, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"n1","method":"session/new","params":{"cwd":"/tmp","mcpServers":[]}}
            """);
        using (JsonDocument answer = await peer.ReceiveResponseAsync("n1", deadline.Token))
        {
            Assert.Equal(ErrorCode.InvalidParams, answer.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        // The rejected session was never kept, so a prompt against one cannot find it.
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"p1","method":"session/prompt","params":{"sessionId":"anything","prompt":[{"type":"text","text":"hi"}]}}
            """);
        using JsonDocument missing = await peer.ReceiveResponseAsync("p1", deadline.Token);
        Assert.Equal(ErrorCode.ResourceNotFound, missing.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task SessionCancelAsync_WithoutActiveTurn_IsIgnoredWithoutResponse()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        string sessionId = await NewSessionAsync(peer, deadline.Token);
        await peer.WriteAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/cancel\",\"params\":{\"sessionId\":\"" + sessionId + "\"}}");
        Drain(peer);
        await EngineTestUtil.AssertNoFrameAsync(peer.Wire, deadline.Token);
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task SessionLimit_Exhausted_AnswersResourceLimit()
    {
        using CancellationTokenSource deadline = Deadline();
        (LoopbackTransport clientWire, LoopbackTransport agentWire) = LoopbackTransport.CreatePair();
        AcpAgentConnection agent = new(
            agentWire,
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero),
            new AcpAgentOptions { MaxSessions = 1 });
        await using AcpAgentConnection lifetime = agent;
        agent.Start();
        RawPeer peer = new(clientWire);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"i1","method":"initialize","params":{"protocolVersion":1}}
            """);
        _ = await peer.ReceiveResponseAsync("i1", deadline.Token);
        _ = await NewSessionAsync(peer, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"n2","method":"session/new","params":{"cwd":"/tmp","mcpServers":[]}}
            """);
        using JsonDocument answer = await peer.ReceiveResponseAsync("n2", deadline.Token);
        Assert.Equal(AcpAgentErrorCode.ResourceLimit, answer.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        agentWire.SimulateEof();
        await agent.Completion;
    }

    [Fact]
    public async Task EofAsync_CompletesHostAndDrainsTrackedTasks()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        string sessionId = await NewSessionAsync(peer, deadline.Token);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"p1","method":"session/prompt","params":{"sessionId":"SESSION","prompt":[{"type":"text","text":"hi"}]}}
            """.Replace("SESSION", sessionId));
        _ = await peer.ReceiveResponseAsync("p1", deadline.Token);
        agentWire.SimulateEof();
        await agent.Completion;
        Assert.Equal(0, agent.TrackedTaskCount);
    }

    [Fact]
    public void Builder_WithoutPromptHandler_CannotBuild()
    {
        (LoopbackTransport clientWire, LoopbackTransport agentWire) = LoopbackTransport.CreatePair();
        AcpAgentBuilder builder = new AcpAgentBuilder().WithInfo("x", "1");
        Assert.Throws<InvalidOperationException>(() => new AcpAgentConnection(agentWire, builder));
    }

    [Fact]
    public async Task Builder_AfterConnectionConstruction_IsFrozen()
    {
        using CancellationTokenSource deadline = Deadline();
        (AcpAgentConnection agent, RawPeer peer, LoopbackTransport agentWire) = await CreateStartedAsync(
            EchoAgentDefinition.CreateBuilder(TimeSpan.Zero), deadline.Token);
        await using AcpAgentConnection lifetime = agent;
        AcpAgentBuilder late = EchoAgentDefinition.CreateBuilder(TimeSpan.Zero);
        Assert.Throws<InvalidOperationException>(() => late.HandlePrompt((_, _) => Task.FromResult(StopReason.EndTurn)));
        agentWire.SimulateEof();
        await agent.Completion;
    }

    /// <summary>Creates and starts the agent; the raw peer completes initialize on its behalf.</summary>
    private static async Task<(AcpAgentConnection Agent, RawPeer Peer, LoopbackTransport AgentWire)> CreateStartedAsync(
        AcpAgentBuilder builder,
        CancellationToken deadline)
    {
        (LoopbackTransport clientWire, LoopbackTransport agentWire) = LoopbackTransport.CreatePair();
        AcpAgentConnection agent = new(agentWire, builder);
        agent.Start();
        RawPeer peer = new(clientWire);
        return (agent, peer, agentWire);
    }

    private static async Task InitializeAsync(RawPeer peer, CancellationToken deadline)
    {
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":1}}
            """);
        _ = await peer.ReceiveResponseAsync("init", deadline);
    }

    /// <summary>Emits frames the handshake left on the tap, so silence assertions start clean.</summary>
    private static void Drain(RawPeer peer)
    {
        while (peer.Wire.OutgoingFrames.TryRead(out _)) { }
    }

    private static async Task<string> NewSessionAsync(RawPeer peer, CancellationToken deadline)
    {
        await InitializeAsync(peer, deadline);
        await peer.WriteAsync("""
            {"jsonrpc":"2.0","id":"n0","method":"session/new","params":{"cwd":"/tmp","mcpServers":[]}}
            """);
        using JsonDocument answer = await peer.ReceiveResponseAsync("n0", deadline);
        return answer.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!;
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(15));
}
