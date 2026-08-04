using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class ConversationQueryService : IConversationQueryService
{
    private readonly IConversationStore _store;
    private readonly IConversationRepository? _coldArchive;
    private readonly ILogger<ConversationQueryService> _logger;

    public ConversationQueryService(
        IConversationStore store,
        ILogger<ConversationQueryService> logger,
        IConversationRepository? coldArchive = null)
    {
        _store = store;
        _coldArchive = coldArchive;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (_coldArchive == null)
        {
            var hotOnlyResults = await _store.ListConversationsAsync(tenantId, skip, take, cancellationToken);
            return hotOnlyResults;
        }

        if (take <= 0)
        {
            return Array.Empty<ConversationRecord>();
        }

        var normalizedSkip = Math.Max(skip, 0);
        var candidateTake = CalculateCandidateTake(normalizedSkip, take);
        var hotResults = await _store.ListConversationsAsync(tenantId, 0, candidateTake, cancellationToken);

        try
        {
            var coldResults = await _coldArchive.ListConversationsAsync(tenantId, 0, candidateTake, cancellationToken);
            return MergeRecords(hotResults, coldResults, normalizedSkip, take);
        }
        catch (Exception ex)
        {
            ConversationLog.ColdArchiveListFailed(_logger, ex, tenantId);
            return hotResults
                .OrderByDescending(r => r.LastMessageAt)
                .Skip(normalizedSkip)
                .Take(take)
                .ToList()
                .AsReadOnly();
        }
    }

    public async Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (_coldArchive == null)
        {
            var hotOnlyResults = await _store.SearchConversationsAsync(tenantId, keyword, skip, take, cancellationToken);
            return hotOnlyResults;
        }

        if (take <= 0)
        {
            return Array.Empty<ConversationRecord>();
        }

        var normalizedSkip = Math.Max(skip, 0);
        var candidateTake = CalculateCandidateTake(normalizedSkip, take);
        var hotResults = await _store.SearchConversationsAsync(tenantId, keyword, 0, candidateTake, cancellationToken);

        try
        {
            var coldResults = await _coldArchive.SearchConversationsAsync(tenantId, keyword, 0, candidateTake, cancellationToken);
            return MergeRecords(hotResults, coldResults, normalizedSkip, take);
        }
        catch (Exception ex)
        {
            ConversationLog.ColdArchiveSearchFailed(_logger, ex, tenantId);
            return hotResults
                .OrderByDescending(r => r.LastMessageAt)
                .Skip(normalizedSkip)
                .Take(take)
                .ToList()
                .AsReadOnly();
        }
    }

    public Task<bool> SoftDeleteAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        return _store.SoftDeleteAsync(tenantId, conversationId, cancellationToken);
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetRecordAsync(tenantId, conversationId, cancellationToken);
        if (record != null)
        {
            return record;
        }

        if (_coldArchive == null)
        {
            return null;
        }

        try
        {
            return await _coldArchive.GetRecordAsync(tenantId, conversationId, cancellationToken);
        }
        catch (Exception ex)
        {
            ConversationLog.QueryColdArchiveGetRecordFailed(_logger, ex, conversationId);
            return null;
        }
    }

    /// <summary>
    /// Merge hot and cold results: hot records take precedence (by ConversationId dedup),
    /// then sort by LastMessageAt desc and apply paging.
    /// </summary>
    private static IReadOnlyList<ConversationRecord> MergeRecords(
        IReadOnlyList<ConversationRecord> hotResults,
        IReadOnlyList<ConversationRecord> coldResults,
        int skip, int take)
    {
        if (take <= 0)
        {
            return Array.Empty<ConversationRecord>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<ConversationRecord>(hotResults.Count + coldResults.Count);
        foreach (var hot in hotResults)
        {
            if (seen.Add(hot.ConversationId))
            {
                merged.Add(hot);
            }
        }

        foreach (var cold in coldResults)
        {
            if (seen.Add(cold.ConversationId))
            {
                merged.Add(cold);
            }
        }

        return merged
            .OrderByDescending(r => r.LastMessageAt)
            .Skip(skip)
            .Take(take)
            .ToList()
            .AsReadOnly();
    }

    private static int CalculateCandidateTake(int skip, int take)
    {
        return skip > int.MaxValue - take
            ? int.MaxValue
            : skip + take;
    }
}
