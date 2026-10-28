using OpenAgent.Core.Conversation.Lock;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class InMemoryConversationLockTests
{
    [Fact]
    public async Task TryAcquireAsync_FreeLock_Acquired()
    {
        var lockObj = new InMemoryConversationLock();
        await using var handle = await lockObj.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(10));
        Assert.NotNull(handle);
        Assert.True(handle.IsHeld);
    }

    [Fact]
    public async Task TryAcquireAsync_SameKeyAlreadyHeld_ReturnsNull()
    {
        var lockObj = new InMemoryConversationLock();
        await using var first = await lockObj.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(10));
        Assert.NotNull(first);

        var second = await lockObj.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(10));
        Assert.Null(second);
    }

    [Fact]
    public async Task Dispose_ReleasesLock_AllowsReacquire()
    {
        var lockObj = new InMemoryConversationLock();
        var first = await lockObj.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(10));
        Assert.NotNull(first);

        await first.DisposeAsync();
        Assert.False(first.IsHeld);

        await using var second = await lockObj.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(10));
        Assert.NotNull(second);
        Assert.True(second.IsHeld);
    }

    [Fact]
    public async Task TryAcquireAsync_DifferentKeys_AreIndependent()
    {
        var lockObj = new InMemoryConversationLock();
        await using var h1 = await lockObj.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(10));
        await using var h2 = await lockObj.TryAcquireAsync("t1", "c2", TimeSpan.FromSeconds(10));

        Assert.NotNull(h1);
        Assert.NotNull(h2);
    }

    [Fact]
    public async Task Handle_HasUniqueOwnerToken()
    {
        var lockObj = new InMemoryConversationLock();
        var h1 = await lockObj.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(10));
        var h2 = await lockObj.TryAcquireAsync("t1", "c2", TimeSpan.FromSeconds(10));

        Assert.NotNull(h1);
        Assert.NotNull(h2);
        Assert.NotEqual(h1.OwnerToken, h2.OwnerToken);
    }
}
