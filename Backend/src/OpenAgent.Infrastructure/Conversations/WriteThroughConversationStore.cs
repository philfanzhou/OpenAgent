using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Infrastructure;

/// <summary>
/// Keeps the durable store authoritative and writes a successful mutation to
/// the optional hot cache. Cache failures are observable but never make a
/// committed conversation unavailable.
/// </summary>
internal sealed class WriteThroughConversationStore(
    IConversationStore durable,
    IConversationCache cache,
    ILogger<WriteThroughConversationStore> logger) : IConversationStore
{
    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default)
    {
        ConversationRecord? cached = await TryGetCachedAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (cached != null)
        {
            return cached.Messages.TakeLast(maxMessages).ToArray();
        }

        IReadOnlyList<ConversationMessage> messages = await durable.GetMessagesAsync(
            tenantId, conversationId, maxMessages, cancellationToken).ConfigureAwait(false);
        await WarmAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        return messages;
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
        string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default)
    {
        ConversationRecord? cached = await TryGetCachedAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (cached != null)
        {
            return cached.Messages.Skip(Math.Max(0, skip)).Take(take).ToArray();
        }

        IReadOnlyList<ConversationMessage> messages = await durable.GetMessagesPagedAsync(
            tenantId, conversationId, skip, take, cancellationToken).ConfigureAwait(false);
        await WarmAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        return messages;
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        ConversationRecord? cached = await TryGetCachedAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (cached != null)
        {
            return cached;
        }

        ConversationRecord? record = await durable.GetRecordAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (record != null)
        {
            await TrySetCachedAsync(record, cancellationToken).ConfigureAwait(false);
        }
        return record;
    }

    public async Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        bool created = await durable.CreateAsync(record, cancellationToken).ConfigureAwait(false);
        if (created)
        {
            await TrySetCachedAsync(record, cancellationToken).ConfigureAwait(false);
        }
        return created;
    }

    public async Task<AppendResult> AppendMessagesAsync(
        string tenantId, string conversationId, int expectedVersion, IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken = default)
    {
        AppendResult result = await durable.AppendMessagesAsync(
            tenantId, conversationId, expectedVersion, messages, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            await WarmAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    public async Task<bool> UpdateStatusAsync(
        string tenantId, string conversationId, ConversationStatus status, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        bool updated = await durable.UpdateStatusAsync(
            tenantId, conversationId, status, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (updated)
        {
            await WarmAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        }
        return updated;
    }

    public async Task<bool> UpdateModelOverrideAsync(
        string tenantId,
        string conversationId,
        LlmModelSelection? modelOverride,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        bool updated = await durable.UpdateModelOverrideAsync(
            tenantId,
            conversationId,
            modelOverride,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        if (updated)
        {
            await WarmAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        }
        return updated;
    }

    public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default) =>
        durable.ListConversationsAsync(tenantId, skip, take, cancellationToken);

    public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default) =>
        durable.SearchConversationsAsync(tenantId, keyword, skip, take, cancellationToken);

    public async Task<bool> SoftDeleteAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        bool deleted = await durable.SoftDeleteAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (deleted)
        {
            try
            {
                await cache.RemoveAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Conversation cache invalidation failed for {ConversationId}", conversationId);
            }
        }
        return deleted;
    }

    public async Task<bool> RecordCompressionAsync(
        string tenantId,
        string conversationId,
        ContextSummary summary,
        CancellationToken cancellationToken = default)
    {
        bool recorded = await durable.RecordCompressionAsync(
            tenantId, conversationId, summary, cancellationToken).ConfigureAwait(false);
        if (recorded)
        {
            await WarmAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        }
        return recorded;
    }

    private async Task WarmAsync(string tenantId, string conversationId, CancellationToken cancellationToken)
    {
        ConversationRecord? record = await durable.GetRecordAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (record != null)
        {
            await TrySetCachedAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ConversationRecord?> TryGetCachedAsync(string tenantId, string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            return await cache.GetAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Conversation cache read failed for {ConversationId}", conversationId);
            return null;
        }
    }

    private async Task TrySetCachedAsync(ConversationRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await cache.SetAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Conversation cache write failed for {ConversationId}", record.ConversationId);
        }
    }
}
