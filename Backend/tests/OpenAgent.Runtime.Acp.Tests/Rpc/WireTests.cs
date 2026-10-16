using System.Text;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;
namespace OpenAgent.Runtime.Acp.Tests.Rpc;
public sealed class WireTests
{
    [Fact]
    public async Task Batch_MixedInvalidRequestAndNotification_ReturnsSingleResponseArray()
    {
        var (peer, transport) = DuplexTransport.Pair();
        await using AcpRpcConnection rpc = new(transport);
        rpc.On<Empty, Empty>("echo", (_, _) => Task.FromResult(new Empty())); rpc.Start();
        await peer.WriteAsync(Encoding.UTF8.GetBytes("""[{"jsonrpc":"2.0","method":"echo","params":{},"id":1},42,{"jsonrpc":"2.0","method":"echo","params":{}}]"""));
        JsonElement response = await ReadAsync(peer);
        Assert.Equal(JsonValueKind.Array, response.ValueKind); Assert.Equal(2, response.GetArrayLength());
        Assert.Equal(1, response[0].GetProperty("id").GetInt32()); Assert.Equal(-32600, response[1].GetProperty("error").GetProperty("code").GetInt32());
    }
    [Fact]
    public async Task MalformedNotification_IsSilentAndConnectionRemainsUsable()
    {
        var (peer, transport) = DuplexTransport.Pair();
        await using AcpRpcConnection rpc = new(transport);
        rpc.OnNotification<SessionRequest>("notice", _ => { }); rpc.On<Empty, Empty>("echo", (_, _) => Task.FromResult(new Empty())); rpc.Start();
        await peer.WriteAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"notice","params":{}}"""));
        await peer.WriteAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"echo","id":"ok"}"""));
        Assert.Equal("ok", (await ReadAsync(peer)).GetProperty("id").GetString());
    }
    [Fact]
    public async Task Batch_FollowedImmediatelyByCancel_CancelsRegisteredRequest()
    {
        var (peer, transport) = DuplexTransport.Pair();
        await using AcpRpcConnection rpc = new(transport);
        rpc.On<Empty, Empty>("wait", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(); }); rpc.Start();
        await peer.WriteAsync(Encoding.UTF8.GetBytes("""[{"jsonrpc":"2.0","method":"wait","id":7}]"""));
        await peer.WriteAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"$/cancel_request","params":{"requestId":7}}"""));
        Assert.Equal(-32800, (await ReadAsync(peer))[0].GetProperty("error").GetProperty("code").GetInt32());
    }
    [Theory]
    [InlineData("{", -32700)]
    [InlineData("[]", -32600)]
    [InlineData("{\"jsonrpc\":2,\"id\":1}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"x\",\"id\":1.5}", -32600)]
    public async Task InvalidWire_ReturnsStandardError(string input, int expected)
    {
        var (peer, transport) = DuplexTransport.Pair(); await using AcpRpcConnection rpc = new(transport); rpc.Start();
        await peer.WriteAsync(Encoding.UTF8.GetBytes(input));
        Assert.Equal(expected, (await ReadAsync(peer)).GetProperty("error").GetProperty("code").GetInt32());
    }
    [Fact]
    public async Task BlockedAtomicWrite_RequestDeadlineClosesAndConverges()
    {
        PendingWriteStream output = new();
        await using AcpRpcConnection rpc = new(new OpenAgent.Runtime.Acp.Transport.AcpStreamTransport(new PendingReadStream(), output), new() { RequestTimeout = TimeSpan.FromMilliseconds(50) }); rpc.Start();
        Task<Empty> request = rpc.RequestAsync<Empty, Empty>("wait", new());
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        await rpc.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, rpc.PendingCount);
    }
    private static async Task<JsonElement> ReadAsync(DuplexTransport peer)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5)); using JsonDocument doc = JsonDocument.Parse((await peer.ReadAsync(timeout.Token))!); return doc.RootElement.Clone(); }
}
