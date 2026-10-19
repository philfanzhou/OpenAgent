using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>JSON-RPC batch handling (V03): per-request answers, notification-only silence, empty batch, in-batch cancel.</summary>
public sealed class AcpRpcBatchTests
{
    [Fact]
    public async Task Batch_MixedItems_AnswersOnlyRequestItemsInOrder()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        List<string> notifications = [];
        client.RegisterNotificationHandler("test/note", (notification, _) =>
        {
            lock (notifications) notifications.Add(notification.Method);
            return Task.CompletedTask;
        });
        client.RegisterRequestHandler("test/one", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("first", true);
            writer.WriteEndObject();
        })));
        client.RegisterRequestHandler("test/two", (_, _) => Task.FromResult<JsonElement?>(EngineTestUtil.BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("second", true);
            writer.WriteEndObject();
        })));
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, """
            [
              {"id":"a","method":"test/one","params":{}},
              {"method":"test/note","params":{"n":1}},
              {"id":"b","method":"test/two","params":{}}
            ]
            """);

        JsonDocument? batch = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token);
        Assert.NotNull(batch);
        Assert.Equal(JsonValueKind.Array, batch!.RootElement.ValueKind);
        Assert.Equal(2, batch.RootElement.GetArrayLength());
        Assert.Equal("a", batch.RootElement[0].GetProperty("id").GetString());
        Assert.True(batch.RootElement[0].GetProperty("result").GetProperty("first").GetBoolean());
        Assert.Equal("b", batch.RootElement[1].GetProperty("id").GetString());
        Assert.True(batch.RootElement[1].GetProperty("result").GetProperty("second").GetBoolean());
        await EngineTestUtil.WaitForAsync(() =>
        {
            lock (notifications) return notifications.Count == 1;
        }, test.Token);
    }

    [Fact]
    public async Task Batch_AllNotifications_NoResponseFrame()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        List<string> notifications = [];
        client.RegisterNotificationHandler("test/note", (notification, _) =>
        {
            lock (notifications) notifications.Add(notification.Method);
            return Task.CompletedTask;
        });
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, "[{\"method\":\"test/note\",\"params\":{}},{\"method\":\"test/other\"}]");
        await EngineTestUtil.WaitForAsync(() =>
        {
            lock (notifications) return notifications.Count == 1;
        }, test.Token);
        await EngineTestUtil.AssertNoFrameAsync(clientWire, test.Token);
    }

    [Fact]
    public async Task Batch_Empty_AnsweredInvalidRequest()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        client.Start();
        await RpcTestHarness.SendFrameAsync(agentWire, "[]");
        JsonDocument? response = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token);
        Assert.NotNull(response);
        Assert.Equal(JsonValueKind.Object, response!.RootElement.ValueKind);
        Assert.Equal(ErrorCode.InvalidRequest, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Batch_InBatchCancel_DoesNotWaitForOtherWork()
    {
        (AcpRpcConnection client, AcpRpcConnection agent, LoopbackTransport clientWire, LoopbackTransport agentWire) = RpcTestHarness.Connect();
        await using AcpRpcConnection clientOwned = client;
        await using AcpRpcConnection agentOwned = agent;
        using CancellationTokenSource test = new(RpcTestHarness.TimeoutMs);

        TaskCompletionSource handlerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RegisterRequestHandler("test/slow", async (_, ct) =>
        {
            handlerStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return EngineTestUtil.BuildElement(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
        });
        client.Start();

        await RpcTestHarness.SendFrameAsync(agentWire, """
            [
              {"id":"long","method":"test/slow","params":{}},
              {"method":"$/cancel_request","params":{"requestId":"long"}}
            ]
            """);

        await handlerStarted.Task;
        JsonDocument? batch = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token);
        Assert.NotNull(batch);
        Assert.Equal(1, batch!.RootElement.GetArrayLength());
        Assert.Equal(ErrorCode.RequestCancelled, batch.RootElement[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("long", batch.RootElement[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Batch_ItemInvalid_AnsweredWithItemError()
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

        await RpcTestHarness.SendFrameAsync(agentWire, "[42,{\"id\":\"ok\",\"method\":\"test/echo\",\"params\":{}}]");
        JsonDocument? batch = await RpcTestHarness.ReceiveFrameAsync(clientWire, test.Token);
        Assert.NotNull(batch);
        Assert.Equal(2, batch!.RootElement.GetArrayLength());
        Assert.Equal(ErrorCode.InvalidRequest, batch.RootElement[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.True(batch.RootElement[1].TryGetProperty("result", out _));
    }
}
