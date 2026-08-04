namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Distributed lock for serializing conversation execution across Engine instances.
/// </summary>
public interface IConversationLock
{
    /// <summary>
    /// Try to acquire a distributed lock for the given tenant + conversation.
    /// Returns a handle for release, or null if the lock is already held.
    /// </summary>
    Task<IConversationLockHandle?> TryAcquireAsync(
        string tenantId,
        string conversationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Handle to a held conversation lock. Dispose to release.
/// </summary>
public interface IConversationLockHandle : IAsyncDisposable
{
    string TenantId { get; }
    string ConversationId { get; }
    string OwnerToken { get; }
    bool IsHeld { get; }
}
