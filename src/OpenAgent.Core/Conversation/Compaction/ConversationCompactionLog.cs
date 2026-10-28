using System.Text;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Conversation.Compaction;

internal static partial class ConversationCompactionLog
{
    [LoggerMessage(
        EventId = 1450,
        Level = LogLevel.Warning,
        Message = "Conversation summarization failed and original history was restored. ConversationId={ConversationId}")]
    internal static partial void CompactionRecovered(
        ILogger logger,
        string conversationId,
        Exception? exception);

    [LoggerMessage(
        EventId = 1451,
        Level = LogLevel.Warning,
        Message = "Conversation summarization audit write failed. ConversationId={ConversationId}")]
    internal static partial void AuditWriteFailed(
        ILogger logger,
        string conversationId,
        Exception? exception);

    [LoggerMessage(
        EventId = 1452,
        Level = LogLevel.Warning,
        Message = "Conversation summary was rejected because token savings were insufficient. ConversationId={ConversationId} OriginalTokens={OriginalTokens} GeneratedTokens={GeneratedTokens}")]
    internal static partial void CompactionRejected(
        ILogger logger,
        string conversationId,
        int originalTokens,
        int generatedTokens);
}
