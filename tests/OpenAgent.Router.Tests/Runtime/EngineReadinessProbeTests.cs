using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenAgent.Router.Tests.Runtime;

public class EngineReadinessProbeTests
{
    [Fact]
    public async Task IsReadyAsync_EndpointReturnsSuccess_ReturnsTrue()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = RespondOnceAsync(listener);
        using EngineReadinessProbe probe = CreateProbe();

        bool isReady = await probe.IsReadyAsync($"http://127.0.0.1:{port}");

        Assert.True(isReady);
        await server;
    }

    [Fact]
    public async Task IsReadyAsync_EndpointIsUnreachable_ReturnsFalse()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using EngineReadinessProbe probe = CreateProbe();

        bool isReady = await probe.IsReadyAsync($"http://127.0.0.1:{port}");

        Assert.False(isReady);
    }

    private static EngineReadinessProbe CreateProbe()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RouterSettings:ServiceDiscovery:ReadinessTimeoutMs"] = "500"
            })
            .Build();
        return new EngineReadinessProbe(
            configuration,
            NullLogger<EngineReadinessProbe>.Instance);
    }

    private static async Task RespondOnceAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        string? line;
        do
        {
            line = await reader.ReadLineAsync();
        }
        while (!string.IsNullOrEmpty(line));

        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }
}
