using System.Collections.Concurrent;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Conversation.Lock;

/// <summary>
/// In-memory conversation lock using SemaphoreSlim. For dev/test single-instance scenarios.
/// </summary>
internal sealed class InMemoryConversationLock : IConversationLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public Task<IConversationLockHandle?> TryAcquireAsync(
        string tenantId,
        string conversationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var key = $"{tenantId}:{conversationId}";
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        var acquired = semaphore.Wait(0, cancellationToken);
        if (!acquired)
        {
            return Task.FromResult<IConversationLockHandle?>(null);
        }

        return Task.FromResult<IConversationLockHandle?>(
            new InMemoryConversationLockHandle(this, key, tenantId, conversationId));
    }

    internal void Release(string key)
    {
        if (_locks.TryGetValue(key, out var semaphore))
        {
            try { semaphore.Release(); } catch (ObjectDisposedException) { /* already disposed */ }
        }
    }

    private sealed class InMemoryConversationLockHandle : IConversationLockHandle
    {
        private readonly InMemoryConversationLock _owner;
        private readonly string _key;
        private int _disposed;

        public InMemoryConversationLockHandle(
            InMemoryConversationLock owner,
            string key,
            string tenantId,
            string conversationId)
        {
            _owner = owner;
            _key = key;
            TenantId = tenantId;
            ConversationId = conversationId;
            OwnerToken = Guid.NewGuid().ToString("N");
        }

        public string TenantId { get; }
        public string ConversationId { get; }
        public string OwnerToken { get; }
        public bool IsHeld => Volatile.Read(ref _disposed) == 0;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            _owner.Release(_key);
            return ValueTask.CompletedTask;
        }
    }
}
