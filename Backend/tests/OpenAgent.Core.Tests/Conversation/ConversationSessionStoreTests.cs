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
            Summary = "Earlier conversation summary",
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
    public void ResolveModelHistory_AutomaticCompression_UsesLatestProjection()
    {
        ConversationRecord record = RecordWithMessages(4);
        record.ContextSummaries.Add(new ContextSummary
        {
            CompressionId = "compression-1",
            Strategy = "summarization",
            Trigger = "Automatic",
            Status = "Succeeded",
            Summary = "automatic projection",
            SourceEndSequence = 4,
            CompactedMessages = [Message(1, "assistant", "automatic projection")]
        });

        IReadOnlyList<ConversationMessage> modelHistory =
            ConversationSessionStore.ResolveModelHistory(record);

        Assert.Equal(["automatic projection"], modelHistory.Select(message => message.Content));
    }

    [Fact]
    public void ResolveModelHistory_UsesLatestCompressionAndAppendsOnlyNewMessages()
    {
        ConversationRecord record = RecordWithMessages(8);
        record.ContextSummaries.Add(new ContextSummary
        {
            CompressionId = "compression-1",
            Strategy = "summarization",
            Trigger = "Automatic",
            Status = "Succeeded",
            Summary = "Automatic summary",
            SourceEndSequence = 6,
            CompactedMessages =
            [
                Message(1, "summary", "Automatic summary"),
                Message(2, "user", "message-6")
            ]
        });

        IReadOnlyList<ConversationMessage> modelHistory =
            ConversationSessionStore.ResolveModelHistory(record);

        Assert.Equal(
            ["Automatic summary", "message-6", "message-7", "message-8"],
            modelHistory.Select(message => message.Content));
    }

    [Fact]
    public void ResolveModelHistory_UnavailableSummary_FallsBackToPreviousSuccessfulProjection()
    {
        ConversationRecord record = RecordWithMessages(8);
        record.ContextSummaries.Add(new ContextSummary
        {
            CompressionId = "compression-valid",
            Strategy = "summarization",
            Trigger = "Manual",
            Status = "Succeeded",
            Summary = "Useful prior state",
            SourceEndSequence = 6,
            CompactedMessages = [Message(1, "assistant", "Useful prior state")]
        });
        record.ContextSummaries.Add(new ContextSummary
        {
            CompressionId = "compression-unavailable",
            Strategy = "summarization",
            Trigger = "Manual",
            Status = "Succeeded",
            Summary = "[Summary]\n[Summary unavailable]",
            SourceEndSequence = 8,
            CompactedMessages = [Message(1, "assistant", "[Summary unavailable]")]
        });

        IReadOnlyList<ConversationMessage> modelHistory =
            ConversationSessionStore.ResolveModelHistory(record);

        Assert.Equal(
            ["Useful prior state", "message-7", "message-8"],
            modelHistory.Select(message => message.Content));
    }

    [Fact]
    public async Task SaveAsync_StampsTurnTraceIdOnAllMessagesOfTheTurn()
    {
        var store = new OpenAgent.Core.Conversation.Store.InMemoryConversationStore(new FakeUserContext());
        var sessionStore = new ConversationSessionStore(
            store,
            Microsoft.Extensions.Options.Options.Create(new ConversationStoreOptions()));
        await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            Type = ConversationType.User,
            Version = 1
        });
        var context = new ConversationContext(
            "conversation-1",
            "tenant-1",
            "user-1",
            "agent-1",
            "trace-42",
            ConversationType.User);

        await sessionStore.SaveAsync(
            context,
            expectedVersion: 1,
            [Message(1, "user", "hi"), Message(2, "assistant", "ok")],
            ConversationStatus.Completed,
            CancellationToken.None);

        IReadOnlyList<ConversationMessage> stored = await store.GetMessagesAsync(
            "tenant-1",
            "conversation-1",
            maxMessages: 10);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, message => Assert.Equal("trace-42", message.TraceId));
    }

    [Fact]
    public async Task SaveAsync_VersionConflictRetry_PreservesMessageIdentityAndIdempotencyKey()
    {
        var store = new OpenAgent.Core.Conversation.Store.InMemoryConversationStore(new FakeUserContext());
        var sessionStore = new ConversationSessionStore(
            store,
            Microsoft.Extensions.Options.Options.Create(new ConversationStoreOptions()));
        await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            Type = ConversationType.User,
            Version = 1
        });
        // 并发写入使版本前进，触发本侧追加的乐观锁冲突重试。
        await store.AppendMessagesAsync(
            "tenant-1",
            "conversation-1",
            expectedVersion: 1,
            [Message(1, "user", "concurrent")]);
        var timestamp = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var retryMessage = new ConversationMessage
        {
            MessageId = "message-retry-1",
            Sequence = 1,
            Role = "assistant",
            Content = "retried",
            IdempotencyKey = "idem-1",
            Timestamp = timestamp
        };
        var context = new ConversationContext(
            "conversation-1",
            "tenant-1",
            "user-1",
            "agent-1",
            "trace-42",
            ConversationType.User);

        await sessionStore.SaveAsync(
            context,
            expectedVersion: 1,
            [retryMessage],
            ConversationStatus.Completed,
            CancellationToken.None);

        IReadOnlyList<ConversationMessage> stored = await store.GetMessagesAsync(
            "tenant-1",
            "conversation-1",
            maxMessages: 10);
        ConversationMessage retried = Assert.Single(stored, message => message.MessageId == "message-retry-1");
        Assert.Equal(2, retried.Sequence);
        Assert.Equal("idem-1", retried.IdempotencyKey);
        Assert.Equal(timestamp, retried.Timestamp);
        Assert.Equal("trace-42", retried.TraceId);
    }

    private sealed class FakeUserContext : Contracts.Security.ICurrentUserContext
    {
        public string UserId => "user-1";
        public string? TenantId => "tenant-1";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
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
