using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public sealed class ConversationSessionStoreTests
{
    [Fact]
    public void ResolveModelHistory_ManualCompression_UsesProjectionAndAppendsNewMessages()
    {
        ConversationRecord record = RecordWithMessages(8);
        record.ContextSummaries.Add(new ContextSummary
        {
            CompressionId = "compression-1",
            Strategy = "summarization",
            Trigger = "Manual",
            Status = "Succeeded",
            SourceEndSequence = 6,
            CompactedMessages =
            [
                Message(1, "summary", "Earlier conversation summary"),
                Message(2, "assistant", "Most recent retained response")
            ]
        });

        IReadOnlyList<ConversationMessage> modelHistory =
            ConversationSessionStore.ResolveModelHistory(record);

        Assert.Equal(
            ["Earlier conversation summary", "Most recent retained response", "message-7", "message-8"],
            modelHistory.Select(message => message.Content));
        Assert.Equal(8, record.Messages.Count);
    }

    [Fact]
    public void ResolveModelHistory_AutomaticCompression_KeepsCompleteStoredHistory()
    {
        ConversationRecord record = RecordWithMessages(4);
        record.ContextSummaries.Add(new ContextSummary
        {
            CompressionId = "compression-1",
            Strategy = "truncation",
            Trigger = "Automatic",
            Status = "Succeeded",
            SourceEndSequence = 4,
            CompactedMessages = [Message(1, "assistant", "automatic projection")]
        });

        IReadOnlyList<ConversationMessage> modelHistory =
            ConversationSessionStore.ResolveModelHistory(record);

        Assert.Equal(record.Messages, modelHistory);
    }

    private static ConversationRecord RecordWithMessages(int count) => new()
    {
        ConversationId = "conversation-1",
        TenantId = "tenant-1",
        UserId = "user-1",
        Messages = Enumerable.Range(1, count)
            .Select(sequence => Message(sequence, sequence % 2 == 0 ? "assistant" : "user", $"message-{sequence}"))
            .ToList(),
        MessageCount = count
    };

    private static ConversationMessage Message(int sequence, string role, string content) => new()
    {
        MessageId = $"message-{sequence}-{Guid.NewGuid():N}",
        Sequence = sequence,
        Role = role,
        Content = content
    };
}
