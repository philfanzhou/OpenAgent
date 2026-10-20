using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Client;

public sealed class FileTests
{
    [Fact]
    public async Task FileHandler_RejectsTraversalAndSymlinksBeyondSessionRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            RootedFileSystem files = new(id => id == "owned" ? [root] : [], allowWrite: true);
            string path = Path.Combine(root, "file.txt");
            await files.WriteAsync(new("owned", path, "a\n你好🥐\nc"));
            Assert.Equal("你好🥐", (await files.ReadAsync(new("owned", path, 2, 1))).Content);
            await Assert.ThrowsAsync<AcpRpcException>(() => files.ReadAsync(new("other", path)));
            await Assert.ThrowsAsync<AcpRpcException>(() => files.WriteAsync(new("owned", Path.Combine(root, "..", Path.GetFileName(outside), "bad.txt"), "bad")));
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
                await Assert.ThrowsAsync<AcpRpcException>(() => files.WriteAsync(new("owned", Path.Combine(root, "escape", "bad.txt"), "bad")));
            }
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }
    [Fact]
    public async Task FileHandler_DefaultDoesNotGrantWrites()
    {
        RootedFileSystem files = new(_ => [Path.GetTempPath()]);
        await Assert.ThrowsAsync<AcpRpcException>(() => files.WriteAsync(new("s", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "denied")));
    }
}
