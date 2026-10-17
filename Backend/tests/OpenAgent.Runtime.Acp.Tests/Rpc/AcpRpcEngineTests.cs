using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>Core wire behavior: reentrancy, ID handling, errors, duplicate and late responses (V02/V03/V05).</summary>
public sealed class AcpRpcEngineTests
{
    // Wire directions for the pair: frames written to one side arrive at the other engine.
    // Injecting into the client engine and reading its output both happen on the agent wire.

    [Fact]
    public async Task Request_HandlerWaitsForReverseCall_BothDirectionsComplete()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        agent.RegisterRequestHandler("test/outer", async (_, ct) =>
        {
            // Reentrant reverse call from inside the agent handler.
            JsonElement inner = await agent.RequestAsync("test/inner", null, ct).ConfigureAwait(false);
            return EngineTestUtil.BuildElement(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("seen", inner.GetProperty("ok").GetString());
                writer.WriteEndObject();
            });
        });
        client.RegisterRequestHandler("test/inner", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("ok", "inner");
            writer.WriteEndObject();
        })));
        client.Start();
        agent.Start();

        JsonElement result = await client.RequestAsync("test/outer", null, test.Token).ConfigureAwait(false);
        Assert.Equal("inner", result.GetProperty("seen").GetString());
    }

    [Fact]
    public async Task Request_SameIdBothDirections_ResponsesStaySeparated()
    {
        // Both engines number outbound IDs identically; inbound requests must never touch the pending table.
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect(
            new AcpRpcOptions { OutboundIdPrefix = "" },
            new AcpRpcOptions { OutboundIdPrefix = "" });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        client.RegisterRequestHandler("test/from-agent", (request, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            // Throws when the inbound ID is not a string: engine-generated outbound IDs stay strings.
            writer.WriteString("served", request.Id.StringValue);
            writer.WriteEndObject();
        })));
        agent.RegisterRequestHandler("test/from-client", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("agent", true);
            writer.WriteEndObject();
        })));
        client.Start();
        agent.Start();

        Task<JsonElement> clientCall = client.RequestAsync("test/from-client", null, test.Token);
        Task<JsonElement> agentCall = agent.RequestAsync("test/from-agent", null, test.Token);
        JsonElement clientResult = await clientCall.ConfigureAwait(false);
        JsonElement agentResult = await agentCall.ConfigureAwait(false);
        Assert.True(clientResult.GetProperty("agent").GetBoolean());
        Assert.Equal("1", agentResult.GetProperty("served").GetString());
    }

    [Theory]
    [InlineData("\"str-42\"", "\"str-42\"")]
    [InlineData("9007199254740993", "9007199254740993")]
    public async Task Request_PeerIdKinds_EchoVerbatim(string rawId, string expectedRaw)
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        client.RegisterRequestHandler("test/echo", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        })));
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, $"{{\"id\":{rawId},\"method\":\"test/echo\",\"params\":{{}}}}").ConfigureAwait(false);
        JsonDocument? response = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token).ConfigureAwait(false);
        Assert.NotNull(response);
        Assert.Equal(expectedRaw, response!.RootElement.GetProperty("id").GetRawText());
    }

    [Fact]
    public async Task Request_NullOrFractionalId_AnsweredInvalidRequest()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        client.Start();
        await RpcTestHarness.SendFrameAsync(agentWire, "{\"id\":null,\"method\":\"test/echo\"}").ConfigureAwait(false);
        JsonDocument? first = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token).ConfigureAwait(false);
        Assert.Equal(ErrorCode.InvalidRequest, first!.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        await RpcTestHarness.SendFrameAsync(agentWire, "{\"id\":1.5,\"method\":\"test/echo\"}").ConfigureAwait(false);
        JsonDocument? second = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token).ConfigureAwait(false);
        Assert.Equal(ErrorCode.InvalidRequest, second!.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Request_UnknownMethod_RespondsMethodNotFound()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        client.Start();
        await RpcTestHarness.SendFrameAsync(agentWire, "{\"id\":7,\"method\":\"nope/missing\",\"params\":{}}").ConfigureAwait(false);
        JsonDocument? response = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token).ConfigureAwait(false);
        Assert.Equal(ErrorCode.MethodNotFound, response!.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(7, response.RootElement.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Notification_UnknownOrUnregistered_IsIgnoredWithoutResponse()
    {
        List<AcpRpcDiagnosticEvent> diagnostics = [];
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect(
            new AcpRpcOptions { Diagnostic = diagnostics.Add });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        List<AcpRpcNotification> received = [];
        client.RegisterNotificationHandler("test/known", (notification, _) =>
        {
            received.Add(notification);
            return Task.CompletedTask;
        });
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, "{\"method\":\"test/unknown\",\"params\":{}}").ConfigureAwait(false);
        await RpcTestHarness.SendFrameAsync(agentWire, "{\"method\":\"test/known\",\"params\":{\"value\":1}}").ConfigureAwait(false);
        await EngineTestUtil.WaitForAsync(() => received.Count == 1, test.Token).ConfigureAwait(false);
        Assert.Equal("test/known", received[0].Method);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Kind == AcpRpcDiagnosticKind.UnhandledNotification);
        await EngineTestUtil.AssertNoFrameAsync(clientWire, test.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task Frame_MalformedJson_AnsweredParseErrorAndReaderContinues()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        client.RegisterRequestHandler("test/echo", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        })));
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, "{\"id\":1,\"method\":").ConfigureAwait(false);
        JsonDocument? parseError = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token).ConfigureAwait(false);
        Assert.Equal(ErrorCode.ParseError, parseError!.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        await RpcTestHarness.SendFrameAsync(agentWire, "{\"id\":2,\"method\":\"test/echo\",\"params\":{}}").ConfigureAwait(false);
        JsonDocument? response = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token).ConfigureAwait(false);
        Assert.Equal(2, response!.RootElement.GetProperty("id").GetInt64());
        Assert.True(response.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Response_DuplicateAndUnknown_CollapsedIntoDiagnostics()
    {
        List<AcpRpcDiagnosticEvent> diagnostics = [];
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect(
            new AcpRpcOptions { Diagnostic = diagnostics.Add });
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
                agent.RegisterRequestHandler("test/echo", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        })));
        client.Start();
        agent.Start();

        Task<JsonElement> call = client.RequestAsync("test/echo", null, test.Token);
        // The agent engine writes the reply; its tap is where the response appears.
        JsonDocument? reply = await ReceiveResponseFrameAsync(agentWire, test.Token).ConfigureAwait(false);
        // The same answer twice, plus a response for an ID nobody issued.
        await RpcTestHarness.SendFrameAsync(agentWire, reply!.RootElement.GetRawText()).ConfigureAwait(false);
        await RpcTestHarness.SendFrameAsync(agentWire, reply.RootElement.GetRawText()).ConfigureAwait(false);
        await RpcTestHarness.SendFrameAsync(agentWire, "{\"id\":\"never-issued\",\"result\":{}}").ConfigureAwait(false);
        await call.ConfigureAwait(false);

        await EngineTestUtil.WaitForAsync(() => diagnostics.Any(item => item.Kind == AcpRpcDiagnosticKind.DuplicateResponse), test.Token).ConfigureAwait(false);
        await EngineTestUtil.WaitForAsync(() => diagnostics.Any(item => item.Kind == AcpRpcDiagnosticKind.UnmatchedResponse), test.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task Request_KnownMethodWrongDirection_AnsweredMethodNotFound()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        // session/update is an agent_to_client notification; the agent must refuse it in its own direction.
        agent.RegisterNotificationHandler("session/update", (_, _) => Task.CompletedTask);
        client.Start();
        agent.Start();

        await RpcTestHarness.SendFrameAsync(clientWire, "{\"id\":5,\"method\":\"session/update\",\"params\":{}}").ConfigureAwait(false);
        JsonDocument? response = await RpcTestHarness.ReceiveFrameAsync(agentWire, test.Token).ConfigureAwait(false);
        Assert.Equal(ErrorCode.MethodNotFound, response!.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Request_RegisteredKnownMethodBadPayload_AnsweredInvalidParams()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        // An agent-to-client method the client serves; {} fails the method schema.
        client.RegisterRequestHandler("session/request_permission", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("outcome", "cancelled");
            writer.WriteEndObject();
        })));
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, "{\"id\":9,\"method\":\"session/request_permission\",\"params\":{}}").ConfigureAwait(false);
        JsonDocument? response = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token).ConfigureAwait(false);
        Assert.Equal(ErrorCode.InvalidParams, response!.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Request_ResponseError_PreservedVerbatim()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        agent.RegisterRequestHandler("test/fail", (_, _) => throw new AcpRpcException(new Error(-32000, "needs authentication")));
        client.Start();
        agent.Start();

        AcpRpcException failure = await Assert.ThrowsAsync<AcpRpcException>(
            () => client.RequestAsync("test/fail", null, test.Token)).ConfigureAwait(false);
        Assert.Equal(-32000, failure.Code);
        Assert.Equal("needs authentication", failure.Error.Message);
    }

    [Fact]
    public async Task Response_ResultViolatesMethodSchema_CallFailsConnectionContinues()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        // initialize's result schema requires protocolVersion; the peer sends an invalid result.
        agent.RegisterRequestHandler("initialize", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("unexpected", "shape");
            writer.WriteEndObject();
        })));
        client.Start();
        agent.Start();

        // The engine refuses to write a malformed result and answers Internal Error instead.
        AcpRpcException failure = await Assert.ThrowsAsync<AcpRpcException>(() => client.RequestAsync("initialize", EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", 1);
            writer.WriteEndObject();
        }), test.Token)).ConfigureAwait(false);
        Assert.Equal(ErrorCode.InternalError, failure.Code);

        // The connection stays usable: the same handler answers again with the same -32603,
        // which only a live engine can deliver (a closed one throws AcpConnectionClosedException).
        AcpRpcException again = await Assert.ThrowsAsync<AcpRpcException>(() => client.RequestAsync("initialize", EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", 1);
            writer.WriteEndObject();
        }), test.Token)).ConfigureAwait(false);
        Assert.Equal(ErrorCode.InternalError, again.Code);
    }

    /// <summary>Reads frames until one is a response; the tap also logs requests this engine wrote.</summary>
    private static async Task<JsonDocument?> ReceiveResponseFrameAsync(LoopbackTransport wire, CancellationToken deadline)
    {
        while (true)
        {
            JsonDocument? frame = await RpcTestHarness.ReceiveFrameAsync(wire, deadline).ConfigureAwait(false);
            if (frame is null) return null;
            if (frame.RootElement.ValueKind != JsonValueKind.Object || !frame.RootElement.TryGetProperty("method", out _)) return frame;
        }
    }

    [Fact]
    public async Task Registration_AfterStart_OrDuplicate_IsRejected()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;

        client.RegisterRequestHandler("test/mine", (_, _) => Task.FromResult<JsonElement?>(default));
        Assert.Throws<ArgumentException>(() =>
            client.RegisterRequestHandler("test/mine", (_, _) => Task.FromResult<JsonElement?>(default)));
        client.Start();
        Assert.Throws<InvalidOperationException>(() =>
            client.RegisterRequestHandler("test/late", (_, _) => Task.FromResult<JsonElement?>(default)));
        Assert.Throws<InvalidOperationException>(client.Start);
    }
}
