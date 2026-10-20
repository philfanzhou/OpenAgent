using System.Collections.Concurrent;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;

namespace OpenAgent.OpenCodeDevHost;

/// <summary>
/// 开发宿主的进程内会话存储：无并发租户隔离与持久化，重启即失。
/// 语义对齐 Framework 的 EF 实现（乐观版本、状态流转、软删除），仅供本机联调。
/// </summary>
internal sealed class DevConversationStore(ICurrentUserContext currentUser)
{
    private readonly ConcurrentDictionary<(string Tenant, string Id), ConversationRecord> _records = [];

    internal Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue((tenantId, conversationId), out ConversationRecord? record))
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>(
                record.Messages.TakeLast(maxMessages).ToList());
        }
        return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);
    }

    internal Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
        string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue((tenantId, conversationId), out ConversationRecord? record))
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>(
                record.Messages.Skip(skip).Take(take).ToList());
        }
        return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);
    }

    internal Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.TryGetValue((tenantId, conversationId), out ConversationRecord? record)
            ? record
            : null);

    internal Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.TryAdd((record.TenantId, record.ConversationId), record));

    internal Task<AppendResult> AppendMessagesAsync(
        string tenantId, string conversationId, int expectedVersion,
        IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue((tenantId, conversationId), out ConversationRecord? record))
        {
            return Task.FromResult(AppendResult.Conflict("Conversation not found."));
        }
        lock (record)
        {
            if (record.Version != expectedVersion)
            {
                return Task.FromResult(AppendResult.Conflict(
                    $"Version mismatch: expected {expectedVersion}, actual {record.Version}."));
            }
            foreach (ConversationMessage message in messages)
            {
                record.Messages.Add(message);
            }
            record.Version++;
            record.MessageCount = record.Messages.Count;
            record.LastMessageAt = DateTimeOffset.UtcNow;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(AppendResult.Ok(record.Version, messages.Count));
        }
    }

    internal Task<bool> UpdateStatusAsync(
        string tenantId, string conversationId, ConversationStatus status, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue((tenantId, conversationId), out ConversationRecord? record))
        {
            return Task.FromResult(false);
        }
        lock (record)
        {
            if (record.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }
            record.Status = status;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    internal Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationRecord>>(_records.Values
            .Where(record => record.TenantId == tenantId
                && !record.IsDeletedByUser
                && string.Equals(record.UserId, currentUser.UserId, StringComparison.Ordinal))
            .OrderByDescending(record => record.LastMessageAt)
            .Skip(skip)
            .Take(take)
            .Select(CloneWithoutMessages)
            .ToList());

    internal Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationRecord>>(_records.Values
            .Where(record => record.TenantId == tenantId
                && !record.IsDeletedByUser
                && string.Equals(record.UserId, currentUser.UserId, StringComparison.Ordinal)
                && record.Messages.Any(message => message.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(record => record.LastMessageAt)
            .Skip(skip)
            .Take(take)
            .Select(CloneWithoutMessages)
            .ToList());

    internal Task<bool> SoftDeleteAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue((tenantId, conversationId), out ConversationRecord? record))
        {
            return Task.FromResult(false);
        }
        lock (record)
        {
            record.IsDeletedByUser = true;
            record.DeletedAt = DateTimeOffset.UtcNow;
        }
        return Task.FromResult(true);
    }

    internal Task<bool> RecordCompressionAsync(
        string tenantId, string conversationId, ContextSummary summary, CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue((tenantId, conversationId), out ConversationRecord? record))
        {
            return Task.FromResult(false);
        }
        record.ContextSummaries.Add(summary);
        return Task.FromResult(true);
    }

    /// <summary>以适配器把同一实例暴露为平台存储接口。</summary>
    internal sealed class Adapter(DevConversationStore store) : IConversationStore
    {
        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default) =>
            store.GetMessagesAsync(tenantId, conversationId, maxMessages, cancellationToken);
        public Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default) =>
            store.GetMessagesPagedAsync(tenantId, conversationId, skip, take, cancellationToken);
        public Task<ConversationRecord?> GetRecordAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            store.GetRecordAsync(tenantId, conversationId, cancellationToken);
        public Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default) =>
            store.CreateAsync(record, cancellationToken);
        public Task<AppendResult> AppendMessagesAsync(string tenantId, string conversationId, int expectedVersion, IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default) =>
            store.AppendMessagesAsync(tenantId, conversationId, expectedVersion, messages, cancellationToken);
        public Task<bool> UpdateStatusAsync(string tenantId, string conversationId, ConversationStatus status, int expectedVersion, CancellationToken cancellationToken = default) =>
            store.UpdateStatusAsync(tenantId, conversationId, status, expectedVersion, cancellationToken);
        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(string tenantId, int skip, int take, CancellationToken cancellationToken = default) =>
            store.ListConversationsAsync(tenantId, skip, take, cancellationToken);
        public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default) =>
            store.SearchConversationsAsync(tenantId, keyword, skip, take, cancellationToken);
        public Task<bool> SoftDeleteAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            store.SoftDeleteAsync(tenantId, conversationId, cancellationToken);
        public Task<bool> RecordCompressionAsync(string tenantId, string conversationId, ContextSummary summary, CancellationToken cancellationToken = default) =>
            store.RecordCompressionAsync(tenantId, conversationId, summary, cancellationToken);
    }

    private static ConversationRecord CloneWithoutMessages(ConversationRecord record) => new()
    {
        ConversationId = record.ConversationId,
        TenantId = record.TenantId,
        UserId = record.UserId,
        Type = record.Type,
        AgentId = record.AgentId,
        TraceId = record.TraceId,
        Version = record.Version,
        Status = record.Status,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        LastMessageAt = record.LastMessageAt,
        MessageCount = record.MessageCount,
        Title = record.Title
    };
}
