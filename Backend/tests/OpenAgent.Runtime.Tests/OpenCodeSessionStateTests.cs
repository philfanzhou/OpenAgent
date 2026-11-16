using System.Text.Json;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeSessionStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acp-checkpoint-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("key", "committed", true)]
    [InlineData("different-model", "committed", false)]
    [InlineData("key", "new-version", false)]
    public async Task Take_AfterRestart_RequiresExactIdentityAndCheckpoint(string key, string checkpoint, bool expected)
    {
        OpenCodeWorkspace.CreatePrivateDirectory(_root);
        string home = "p-" + Guid.NewGuid().ToString("N");
        OpenCodeWorkspace.CreatePrivateDirectory(Path.Combine(_root, home));
        await File.WriteAllTextAsync(Path.Combine(_root, "committed-session.json"), JsonSerializer.Serialize(
            new OpenCodeSessionState.Snapshot("key", home, "native-session", "committed", "schema")));
        await using (OpenCodeSessionState state = new(_root))
        {
            OpenCodeSessionState.Snapshot? restored = await state.TakeAsync(key, checkpoint, CancellationToken.None);
            Assert.Equal(expected, restored is not null);
            if (restored is not null) Assert.Equal("native-session", restored.SessionId);
        }
        // A crash after acquisition cannot restore mutable, uncommitted native state.
        await using OpenCodeSessionState restarted = new(_root);
        Assert.Null(await restarted.TakeAsync("key", "committed", CancellationToken.None));
    }

    [Fact]
    public async Task Lease_SameStateDirectory_RejectsConcurrentWriters()
    {
        OpenCodeWorkspace.CreatePrivateDirectory(_root);
        await using OpenCodeSessionState first = new(_root);
        Assert.Throws<IOException>(() => new OpenCodeSessionState(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
