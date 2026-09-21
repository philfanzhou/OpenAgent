using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public sealed class ConversationTurnLockTests
{
    [Fact]
    public async Task AcquireAsync_LockAlreadyHeld_ThrowsConflict()
    {
        var @lock = new FakeLock(held: true);
        var turnLock = new ConversationTurnLock(@lock);

        AgentException exception = await Assert.ThrowsAsync<AgentException>(
            () => turnLock.AcquireAsync("tenant-1", "conversation-1", TimeSpan.FromSeconds(30)));
        Assert.Equal(AgentErrorCode.Conflict, exception.ErrorCode);
    }

    [Fact]
    public async Task ReleaseAsync_CalledTwice_DisposesHandleOnlyOnce()
    {
        var handle = new FakeHandle();
        var @lock = new FakeLock(held: false, handle: handle);
        var turnLock = new ConversationTurnLock(@lock);
        await turnLock.AcquireAsync("tenant-1", "conversation-1", TimeSpan.FromSeconds(30));

        await turnLock.ReleaseAsync();
        await turnLock.ReleaseAsync();

        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task ReleaseAsync_WithoutAcquire_IsNoOp()
    {
        var turnLock = new ConversationTurnLock(new FakeLock(held: false));

        await turnLock.ReleaseAsync();
    }

    private sealed class FakeLock(bool held, FakeHandle? handle = null) : IConversationLock
    {
        public Task<IConversationLockHandle?> TryAcquireAsync(
            string tenantId, string conversationId, TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IConversationLockHandle?>(held ? null : handle ?? new FakeHandle());
    }

    private sealed class FakeHandle : IConversationLockHandle
    {
        public int DisposeCount { get; private set; }

        public string TenantId => "tenant-1";
        public string ConversationId => "conversation-1";
        public string OwnerToken => "owner";
        public bool IsHeld => true;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
