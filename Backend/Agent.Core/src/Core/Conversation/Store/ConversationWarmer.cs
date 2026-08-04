using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class ConversationWarmer
{
    // Must depend on the concrete RedisConversationStore, never IConversationStore:
    // injecting the interface would create a DualWrite -> Warmer -> IConversationStore -> DualWrite resolution cycle.
    private readonly RedisConversationStore _hotStore;
    private readonly IConversationRepository _coldArchive;
    private readonly ILogger<ConversationWarmer> _logger;

    internal ConversationWarmer(
        RedisConversationStore hotStore,
        IConversationRepository coldArchive,
        ILogger<ConversationWarmer> logger)
    {
        _hotStore = hotStore;
        _coldArchive = coldArchive;
        _logger = logger;
    }

    internal async Task WarmAsync(
        string tenantId,
        string conversationId,
        ConversationRecord coldRecord,
        IReadOnlyList<ConversationMessage> coldMessages,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _hotStore.CreateAsync(coldRecord, cancellationToken).ConfigureAwait(false);
            if (created)
            {
                if (coldMessages.Count > 0)
                {
                    var appendResult = await _hotStore.AppendMessagesAsync(
                        tenantId, conversationId, 1, coldMessages, cancellationToken).ConfigureAwait(false);
                    if (!appendResult.Success)
                    {
                        ConversationLog.WarmUpNewRecordAppendFailed(
                            _logger, conversationId, appendResult.ConflictReason);
                    }
                }
            }
            else
            {
                await AppendMissingMessagesAsync(
                    tenantId, conversationId, coldMessages, cancellationToken, existingLog: true).ConfigureAwait(false);
            }

        }
        catch (Exception exception)
        {
            ConversationLog.WarmUpConversationFailed(_logger, exception, conversationId);
        }
    }

    internal async Task WarmAsync(
        string tenantId,
        string conversationId,
        IReadOnlyList<ConversationMessage> coldMessages,
        CancellationToken cancellationToken)
    {
        try
        {
            var existingRecord = await _hotStore.GetRecordAsync(
                tenantId, conversationId, cancellationToken).ConfigureAwait(false);
            if (existingRecord == null)
            {
                var coldRecord = await _coldArchive.GetRecordAsync(
                    tenantId, conversationId, cancellationToken).ConfigureAwait(false);
                if (coldRecord != null)
                {
                    await WarmAsync(
                        tenantId, conversationId, coldRecord, coldMessages, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await AppendMissingMessagesAsync(
                tenantId, conversationId, coldMessages, cancellationToken, existingLog: false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ConversationLog.WarmUpMessagesFailed(_logger, exception, conversationId);
        }
    }

    private async Task AppendMissingMessagesAsync(
        string tenantId,
        string conversationId,
        IReadOnlyList<ConversationMessage> coldMessages,
        CancellationToken cancellationToken,
        bool existingLog)
    {
        var existingRecord = await _hotStore.GetRecordAsync(
            tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (existingRecord == null || coldMessages.Count == 0)
        {
            return;
        }

        var hotMessages = await _hotStore.GetMessagesAsync(
            tenantId, conversationId, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var hotMessageIds = new HashSet<string>(
            hotMessages.Select(message => message.MessageId), StringComparer.OrdinalIgnoreCase);
        var missingMessages = coldMessages
            .Where(message => !hotMessageIds.Contains(message.MessageId))
            .ToList();
        if (missingMessages.Count == 0)
        {
            return;
        }

        var result = await _hotStore.AppendMessagesAsync(
            tenantId,
            conversationId,
            existingRecord.Version,
            missingMessages,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success && existingLog)
        {
            ConversationLog.WarmUpExistingRecordAppendFailed(
                _logger, conversationId, result.ConflictReason);
        }
        else if (!result.Success)
        {
            ConversationLog.WarmUpMessagesAppendFailed(
                _logger, conversationId, result.ConflictReason);
        }
    }
}
