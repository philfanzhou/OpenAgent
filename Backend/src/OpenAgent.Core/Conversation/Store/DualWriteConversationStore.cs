using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class DualWriteConversationStore : IConversationStore, IDisposable
{
    private readonly IConversationStore _hotStore;
    private readonly IConversationRepository _coldArchive;
    private readonly ConversationStoreOptions _options;
    private readonly ILogger<DualWriteConversationStore> _logger;
    private readonly ConversationWarmer _warmer;
    private readonly CompensationArchiveService _archiveService;

    public DualWriteConversationStore(
        IConversationStore hotStore,
        IConversationRepository coldArchive,
        IOptions<ConversationStoreOptions> options,
        ILogger<DualWriteConversationStore> logger,
        ConversationWarmer warmer,
        CompensationArchiveService archiveService)
    {
        _hotStore = hotStore;
        _coldArchive = coldArchive;
        _options = options.Value;
        _logger = logger;
        _warmer = warmer;
        _archiveService = archiveService;
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default)
    {
        var hotMessages = await _hotStore.GetMessagesAsync(tenantId, conversationId, maxMessages, cancellationToken);
        if (hotMessages.Count > 0)
        {
            return hotMessages;
        }

        // Hot store miss — try cold archive
        if (!_options.EnableColdArchive)
        {
            return hotMessages;
        }

        try
        {
            var coldMessages = await _coldArchive.LoadMessagesAsync(tenantId, conversationId, cancellationToken);
            if (coldMessages.Count > 0)
            {
                // Check if the conversation is soft-deleted in cold archive before warming up
                var coldRecord = await _coldArchive.GetRecordAsync(tenantId, conversationId, cancellationToken);
                if (coldRecord?.IsDeletedByUser == true)
                {
                    return [];
                }

                // Fire-and-forget warm-up: write cold data back to hot store
                _ = _warmer.WarmAsync(tenantId, conversationId, coldMessages, cancellationToken);
                return coldMessages;
            }
        }
        catch (Exception ex)
        {
            ConversationLog.ColdArchiveLoadMessagesFailed(_logger, ex, conversationId);
        }

        return hotMessages;
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
        string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var hotMessages = await _hotStore.GetMessagesPagedAsync(tenantId, conversationId, skip, take, cancellationToken);
        if (hotMessages.Count > 0)
        {
            return hotMessages;
        }

        // Hot store miss — try cold archive
        if (!_options.EnableColdArchive)
        {
            return hotMessages;
        }

        try
        {
            var allColdMessages = await _coldArchive.LoadMessagesAsync(tenantId, conversationId, cancellationToken);
            if (allColdMessages.Count > 0)
            {
                // Check if the conversation is soft-deleted in cold archive before warming up
                var coldRecord = await _coldArchive.GetRecordAsync(tenantId, conversationId, cancellationToken);
                if (coldRecord?.IsDeletedByUser == true)
                {
                    return [];
                }

                // Fire-and-forget warm-up
                _ = _warmer.WarmAsync(tenantId, conversationId, allColdMessages, cancellationToken);
                return allColdMessages.Skip(skip).Take(take).ToList().AsReadOnly();
            }
        }
        catch (Exception ex)
        {
            ConversationLog.ColdArchiveLoadPagedMessagesFailed(_logger, ex, conversationId);
        }

        return hotMessages;
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        var hotRecord = await _hotStore.GetRecordAsync(tenantId, conversationId, cancellationToken);
        if (hotRecord != null)
        {
            return hotRecord;
        }

        // Hot store miss — try cold archive
        if (!_options.EnableColdArchive)
        {
            return null;
        }

        try
        {
            var coldRecord = await _coldArchive.GetRecordAsync(tenantId, conversationId, cancellationToken);
            if (coldRecord != null)
            {
                // Do not return soft-deleted conversations from cold archive
                if (coldRecord.IsDeletedByUser)
                {
                    return null;
                }

                // Load messages from cold archive as well
                var coldMessages = await _coldArchive.LoadMessagesAsync(tenantId, conversationId, cancellationToken);
                coldRecord.Messages = coldMessages.ToList();

                // Fire-and-forget warm-up: write cold data back to hot store
                _ = _warmer.WarmAsync(tenantId, conversationId, coldRecord, coldMessages, cancellationToken);
                return coldRecord;
            }
        }
        catch (Exception ex)
        {
            ConversationLog.ColdArchiveGetRecordFailed(_logger, ex, conversationId);
        }

        return null;
    }

    public async Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        var hotResult = await _hotStore.CreateAsync(record, cancellationToken);

        if (!hotResult)
        {
            ConversationLog.HotStoreCreateFailed(_logger, record.ConversationId);
            return false;
        }

        if (_options.EnableColdArchive)
        {
            _ = _archiveService.ArchiveAsync(record, cancellationToken);
        }

        return true;
    }

    public async Task<AppendResult> AppendMessagesAsync(
        string tenantId, string conversationId, int expectedVersion,
        IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default)
    {
        var result = await _hotStore.AppendMessagesAsync(tenantId, conversationId, expectedVersion, messages, cancellationToken);

        if (!result.Success)
        {
            return result;
        }

        if (_options.EnableColdArchive)
        {
            var record = await _hotStore.GetRecordAsync(tenantId, conversationId, cancellationToken);
            if (record != null)
            {
                _ = _archiveService.ArchiveAsync(record, cancellationToken);
                _ = _archiveService.ArchiveMessagesAsync(tenantId, conversationId, messages, cancellationToken);
            }
        }

        return result;
    }

    public async Task<bool> UpdateStatusAsync(
        string tenantId, string conversationId, ConversationStatus status,
        int expectedVersion, CancellationToken cancellationToken = default)
    {
        var result = await _hotStore.UpdateStatusAsync(tenantId, conversationId, status, expectedVersion, cancellationToken);

        if (result && _options.EnableColdArchive)
        {
            var record = await _hotStore.GetRecordAsync(tenantId, conversationId, cancellationToken);
            if (record != null)
            {
                _ = _archiveService.ArchiveAsync(record, cancellationToken);
            }
        }

        return result;
    }

    public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default)
    {
        return _hotStore.ListConversationsAsync(tenantId, skip, take, cancellationToken);
    }

    public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default)
    {
        return _hotStore.SearchConversationsAsync(tenantId, keyword, skip, take, cancellationToken);
    }

    public async Task<bool> SoftDeleteAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        var result = await _hotStore.SoftDeleteAsync(tenantId, conversationId, cancellationToken);

        if (result && _options.EnableColdArchive)
        {
            var record = await _hotStore.GetRecordAsync(tenantId, conversationId, cancellationToken);
            if (record != null)
            {
                _ = _archiveService.ArchiveAsync(record, cancellationToken);
            }
        }

        return result;
    }

    public void Dispose()
    {
        _coldArchive.Dispose();
    }
}
