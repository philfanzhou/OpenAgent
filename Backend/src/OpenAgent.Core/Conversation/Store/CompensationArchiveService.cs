using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class CompensationArchiveService
{
    private readonly IConversationRepository _coldArchive;
    private readonly ILogger<CompensationArchiveService> _logger;

    internal CompensationArchiveService(
        IConversationRepository coldArchive,
        ILogger<CompensationArchiveService> logger)
    {
        _coldArchive = coldArchive;
        _logger = logger;
    }

    internal async Task ArchiveAsync(ConversationRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _coldArchive.ArchiveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ConversationLog.SqlArchiveFailed(
                _logger, exception, record.ConversationId, record.Version);
        }
    }

    internal async Task ArchiveMessagesAsync(
        string tenantId,
        string conversationId,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            await _coldArchive.ArchiveMessagesAsync(
                tenantId, conversationId, messages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ConversationLog.MessageArchiveFailed(_logger, exception, conversationId);
        }
    }
}
