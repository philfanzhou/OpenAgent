using System.Net.Sockets;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class UnixSocketPathTests
{
    [Fact]
    public async Task LongWorkspacePath_ConnectsAndCleanupPreservesWorkspace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        DirectoryInfo root = Directory.CreateTempSubdirectory("runner-socket-test-");
        string channel = Path.Combine(root.FullName, new string('s', 64), "channel");
        Directory.CreateDirectory(channel);
        string original = Path.Combine(channel, "supervisor.sock");
        string marker = Path.Combine(channel, "workspace.txt");
        await File.WriteAllTextAsync(marker, "keep");
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnixDomainSocketEndPoint(original));
            string connectionAlias;
            using (UnixSocketPath listenerPath = UnixSocketPath.Create(original))
            using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                listener.Bind(new UnixDomainSocketEndPoint(listenerPath.Path));
                listener.Listen(1);
                using UnixSocketPath connectionPath = UnixSocketPath.Create(original);
                connectionAlias = connectionPath.Path;
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
                Task<Socket> accepting = listener.AcceptAsync(deadline.Token).AsTask();
                await client.ConnectAsync(new UnixDomainSocketEndPoint(connectionPath.Path), deadline.Token);
                using Socket accepted = await accepting;
                await client.SendAsync(new byte[] { 42 }, SocketFlags.None, deadline.Token);
                byte[] received = new byte[1];
                Assert.Equal(1, await accepted.ReceiveAsync(received, SocketFlags.None, deadline.Token));
                Assert.Equal(42, received[0]);
            }
            Assert.False(Directory.Exists(Path.GetDirectoryName(connectionAlias)));
            Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
