using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

public sealed class RpcTests
{
    [Fact]
    public async Task RequestAsync_NestedBidirectionalCalls_CompleteWithoutReaderDeadlock()
    {
        AcpRpcConnection? left = null, right = null;
        await using RpcPair pair = await RpcPair.CreateAsync(
            c => { left = c; c.On<JsonElement, JsonElement>("reverse", async (p, ct) => await left.RequestAsync<JsonElement, JsonElement>("leaf", p, ct)); },
            c => { right = c; c.On<JsonElement, JsonElement>("outer", async (p, ct) => await right.RequestAsync<JsonElement, JsonElement>("reverse", p, ct)); c.On<JsonElement, JsonElement>("leaf", (p, _) => Task.FromResult(p)); });
        JsonElement result = await pair.Client.RequestAsync<JsonElement, JsonElement>("outer", AcpJson.Parse("42")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, result.GetInt32());
        Assert.Equal(0, pair.Client.PendingCount);
        Assert.Equal(0, pair.Server.PendingCount);
    }
    [Fact]
    public async Task RequestAsync_ConcurrentRequestsWithReorderedCompletion_AssociateCorrectly()
    {
        await using RpcPair pair = await RpcPair.CreateAsync(server: c => c.On<JsonElement, JsonElement>("echo", async (p, ct) => { await Task.Yield(); return p; }));
        Task<int>[] calls = Enumerable.Range(0, 100).Select(async i => (await pair.Client.RequestAsync<JsonElement, JsonElement>("echo", AcpJson.Parse(i.ToString()))).GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 100), await Task.WhenAll(calls));
    }
    [Fact]
    public async Task RequestAsync_CancelledWhileHandlerWaits_PropagatesAndDrainsPending()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcPair pair = await RpcPair.CreateAsync(server: c => c.On<Empty, Empty>("wait", async (_, ct) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                cancelled.TrySetResult();
            }
            return new();
        }));
        using CancellationTokenSource cts = new();
        Task<Empty> request = pair.Client.RequestAsync<Empty, Empty>("wait", new(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, pair.Client.PendingCount);
    }
    [Fact]
    public async Task RequestAsync_UnknownMethodAndRemoteData_PreserveWireErrors()
    {
        await using RpcPair pair = await RpcPair.CreateAsync(server: c => c.On<Empty, Empty>("fail", (_, _) => throw new AcpRpcException(123, "test", AcpJson.Parse("42"))));
        AcpRpcException unknown = await Assert.ThrowsAsync<AcpRpcException>(() => pair.Client.RequestAsync<Empty, Empty>("missing", new()));
        Assert.Equal(-32601, unknown.Code);
        AcpRpcException error = await Assert.ThrowsAsync<AcpRpcException>(() => pair.Client.RequestAsync<Empty, Empty>("fail", new()));
        Assert.Equal(123, error.Code);
        Assert.Equal(42, error.ErrorData!.Value.GetInt32());
    }
    [Fact]
    public async Task RequestAsync_EofFailsPendingInsteadOfReturningEndTurn()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcPair pair = await RpcPair.CreateAsync(server: c => c.On<Empty, Empty>("wait", async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(); }));
        Task<Empty> request = pair.Client.RequestAsync<Empty, Empty>("wait", new());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pair.Server.DisposeAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => request);
        Assert.Equal(0, pair.Client.PendingCount);
    }
    [Fact]
    public async Task RequestAsync_FullOutgoingQuota_DoesNotBlockReverseControlCalls()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcPair pair = await RpcPair.CreateAsync(client: c => c.On<Empty, Empty>("reverse", (_, _) => Task.FromResult(new Empty())),
            server: c => c.On<Empty, Empty>("wait", async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(); }), options: new()
            {
                MaxPendingRequests = 1
            });
        using CancellationTokenSource cts = new();
        Task<Empty> first = pair.Client.RequestAsync<Empty, Empty>("wait", new(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Client.RequestAsync<Empty, Empty>("wait", new()));
        await pair.Server.RequestAsync<Empty, Empty>("reverse", new()).WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }
}
