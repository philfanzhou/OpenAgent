using System.Collections.Concurrent;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationRecord> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICurrentUserContext _currentUser;

    public InMemoryConversationStore(ICurrentUserContext currentUser) => _currentUser = currentUser;

    public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(tenantId, conversationId);

        if (!_store.TryGetValue(key, out var record))
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>(Array.Empty<ConversationMessage>());
        }

        var result = record.Messages.TakeLast(maxMessages).ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<ConversationMessage>>(result);
    }

    public Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
        string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(tenantId, conversationId);

        if (!_store.TryGetValue(key, out var record))
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>(Array.Empty<ConversationMessage>());
        }

        var result = record.Messages.Skip(skip).Take(take).ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<ConversationMessage>>(result);
    }

    public Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(tenantId, conversationId);

        if (!_store.TryGetValue(key, out var record))
        {
            return Task.FromResult<ConversationRecord?>(null);
        }

        return Task.FromResult<ConversationRecord?>(record);
    }

    public Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(record.TenantId, record.ConversationId);
        var created = _store.TryAdd(key, record);

        return Task.FromResult(created);
    }

    public Task<AppendResult> AppendMessagesAsync(
        string tenantId, string conversationId, int expectedVersion,
        IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(tenantId, conversationId);

        if (!_store.TryGetValue(key, out var record))
        {
            return Task.FromResult(AppendResult.Conflict("Conversation not found"));
        }

        lock (record)
        {
            if (record.Version != expectedVersion)
            {
                return Task.FromResult(AppendResult.Conflict($"Version conflict: expected {expectedVersion}, actual {record.Version}"));
            }

            var existingKeys = new HashSet<string>(
                record.Messages
                    .Where(m => m.IdempotencyKey != null)
                    .Select(m => m.IdempotencyKey!),
                StringComparer.OrdinalIgnoreCase);

            var deduplicated = messages.Where(m =>
                m.IdempotencyKey == null || !existingKeys.Contains(m.IdempotencyKey)).ToList();

            var skippedCount = messages.Count - deduplicated.Count;

            record.Messages.AddRange(deduplicated);
            record.Version++;
            record.MessageCount = record.Messages.Count;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            record.LastMessageAt = deduplicated.Count > 0 ? deduplicated[^1].Timestamp : DateTimeOffset.UtcNow;

            return Task.FromResult(AppendResult.Ok(record.Version, record.MessageCount, skippedCount));
        }
    }

    public Task<bool> UpdateStatusAsync(
        string tenantId, string conversationId, ConversationStatus status,
        int expectedVersion, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(tenantId, conversationId);

        if (!_store.TryGetValue(key, out var record))
        {
            return Task.FromResult(false);
        }

        if (record.Version != expectedVersion)
        {
            return Task.FromResult(false);
        }

        record.Status = status;
        record.Version++;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        return Task.FromResult(true);
    }

    public Task<bool> UpdateModelOverrideAsync(
        string tenantId,
        string conversationId,
        LlmModelSelection? modelOverride,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(tenantId, conversationId);
        if (!_store.TryGetValue(key, out ConversationRecord? record))
        {
            return Task.FromResult(false);
        }

        lock (record)
        {
            if (record.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            record.ModelOverride = modelOverride;
            record.Version++;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var records = _store.Values
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .Where(r => !r.IsDeletedByUser)
            .Where(r => string.Equals(r.UserId, _currentUser.UserId, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Type == ConversationType.User)
            .OrderByDescending(r => r.LastMessageAt)
            .Skip(skip)
            .Take(take)
            .Select(r => StripMessages(r))
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<ConversationRecord>>(records);
    }

    public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default)
    {
        var records = _store.Values
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .Where(r => !r.IsDeletedByUser)
            .Where(r => string.Equals(r.UserId, _currentUser.UserId, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Type == ConversationType.User)
            .Where(r => r.Title != null && r.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || r.Messages.Any(m => m.Content != null && m.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.LastMessageAt)
            .Skip(skip)
            .Take(take)
            .Select(r => StripMessages(r))
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<ConversationRecord>>(records);
    }

    public Task<bool> SoftDeleteAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(tenantId, conversationId);

        if (!_store.TryGetValue(key, out var record))
        {
            return Task.FromResult(false);
        }

        record.IsDeletedByUser = true;
        record.DeletedAt = DateTimeOffset.UtcNow;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        return Task.FromResult(true);
    }

    public Task<bool> RecordCompressionAsync(
        string tenantId,
        string conversationId,
        ContextSummary summary,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(tenantId, conversationId);
        if (!_store.TryGetValue(key, out var record))
        {
            return Task.FromResult(false);
        }

        lock (record)
        {
            record.ContextSummaries.Add(summary);
            record.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return Task.FromResult(true);
    }

    private static ConversationRecord StripMessages(ConversationRecord r) => new()
    {
        ConversationId = r.ConversationId,
        TenantId = r.TenantId,
        UserId = r.UserId,
        Type = r.Type,
        AgentId = r.AgentId,
        ModelOverride = r.ModelOverride,
        TraceId = r.TraceId,
        Version = r.Version,
        Status = r.Status,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        LastMessageAt = r.LastMessageAt,
        MessageCount = r.MessageCount,
        Title = r.Title,
        IsDeletedByUser = r.IsDeletedByUser,
        DeletedAt = r.DeletedAt,
        ArchivedAt = r.ArchivedAt,
        Messages = [],
        ContextSummaries = []
    };

    private static string BuildKey(string tenantId, string conversationId) => $"{tenantId}:{conversationId}";
}
