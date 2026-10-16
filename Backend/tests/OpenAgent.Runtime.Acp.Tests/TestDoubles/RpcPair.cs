using System.Net;
using System.Net.Sockets;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Transport;
namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;
internal sealed class RpcPair : IAsyncDisposable
{
    internal AcpRpcConnection Client { get; }
    internal AcpRpcConnection Server { get; }
    private RpcPair(NetworkStream a, NetworkStream b, AcpConnectionOptions? options)
    { Client = new(new AcpStreamTransport(a, a), options); Server = new(new AcpStreamTransport(b, b), options); }
    internal static async Task<RpcPair> CreateAsync(Action<AcpRpcConnection>? client = null, Action<AcpRpcConnection>? server = null, AcpConnectionOptions? options = null)
    {
        TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        TcpClient a = new() { NoDelay = true }; await a.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        TcpClient b = await listener.AcceptTcpClientAsync(); b.NoDelay = true; listener.Stop();
        RpcPair pair = new(a.GetStream(), b.GetStream(), options); client?.Invoke(pair.Client); server?.Invoke(pair.Server);
        pair.Client.Start(); pair.Server.Start(); return pair;
    }
    public async ValueTask DisposeAsync() => await Task.WhenAll(Client.DisposeAsync().AsTask(), Server.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
}
