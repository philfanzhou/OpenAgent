using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using OpenAgent.Runtime.Acp.Rpc;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>Typed registration and calls through the pinned protocol context, plus known-method wire validation.</summary>
public sealed class AcpRpcTypedApiTests
{
    private static readonly AcpMethod<InitializeRequest, InitializeResponse> Initialize = new("initialize");
    private static readonly AcpNotificationMethod<CancelRequestNotification> CancelRequest = new("$/cancel_request");

    [Fact]
    public async Task TypedInitialize_RoundtripsThroughPinnedContext()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        agent.Register(Initialize, (request, _) => Task.FromResult(new InitializeResponse(
            request.ProtocolVersion,
            default,
            new AuthMethod[] { new(new AuthMethodAgentVariant("oauth", "OAuth")) },
            default,
            default)));
        client.Start();
        agent.Start();

        InitializeResponse response = await client.RequestAsync(
            Initialize,
            new InitializeRequest(1, default, default, default),
            test.Token).ConfigureAwait(false);
        Assert.Equal(1u, response.ProtocolVersion);
        Assert.True(response.AuthMethods.HasValue);
        Assert.Equal("oauth", response.AuthMethods.Value[0].AsAgent().Id);
    }

    [Fact]
    public async Task RawOutbound_ViolatingKnownParams_IsRejectedBeforeSend()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, _, _) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        client.Start();
        agent.Start();

        // initialize without protocolVersion fails strict outbound validation before any frame is written.
        await Assert.ThrowsAsync<JsonException>(() => client.RequestAsync(
            "initialize",
            EngineTestUtil.BuildElement(static writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("clientInfo", "wrong");
                writer.WriteEndObject();
            }),
            test.Token)).ConfigureAwait(false);
    }

    [Fact]
    public async Task TypedRegistration_BadPeerParams_AnswersInvalidParams()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);
        AcpMethod<RequestPermissionRequest, RequestPermissionResponse> permission = new("session/request_permission");

        // An agent-to-client request the client serves with a typed handler; a malformed payload
        // fails the method schema and answers Invalid Params instead of reaching the handler.
        client.Register(permission, (_, _) => Task.FromResult(
            new RequestPermissionResponse(
                new RequestPermissionOutcome(
                    new RequestPermissionOutcomeSelected("option-1", default)),
                default)));
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, "{\"id\":1,\"method\":\"session/request_permission\",\"params\":{\"wrong\":true}}").ConfigureAwait(false);
        JsonDocument? response = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token).ConfigureAwait(false);
        Assert.Equal(ErrorCode.InvalidParams, response!.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }
}
