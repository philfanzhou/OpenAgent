using OpenAgent.Contracts.Conversation;
using OpenAgent.Application.Conversation.Store;
using OpenAgent.Application.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Application.Conversation.Lock;
using Xunit;

namespace OpenAgent.Application.Tests.Conversation;

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
        await using IConversationLockHandle handle = await AcquireLockAsync();
        var store = new OpenAgent.Application.Conversation.Store.InMemoryConversationStore(new FakeUserContext());
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

        int? checkpoint = await sessionStore.SaveAsync(
            context,
            handle,
            expectedVersion: 1,
            [Message(1, "user", "hi"), Message(2, "assistant", "ok")],
            ConversationStatus.Completed,
            CancellationToken.None);

        IReadOnlyList<ConversationMessage> stored = await store.GetMessagesAsync(
            "tenant-1",
            "conversation-1",
            maxMessages: 10);
        Assert.Equal(3, checkpoint);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, message => Assert.Equal("trace-42", message.TraceId));
    }

    [Fact]
    public async Task SaveAsync_VersionConflictRetry_PreservesMessageIdentityAndIdempotencyKey()
    {
        await using IConversationLockHandle handle = await AcquireLockAsync();
        var store = new OpenAgent.Application.Conversation.Store.InMemoryConversationStore(new FakeUserContext());
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

        int? checkpoint = await sessionStore.SaveAsync(
            context,
            handle,
            expectedVersion: 1,
            [retryMessage],
            ConversationStatus.Completed,
            CancellationToken.None);

        IReadOnlyList<ConversationMessage> stored = await store.GetMessagesAsync(
            "tenant-1",
            "conversation-1",
            maxMessages: 10);
        Assert.Null(checkpoint);
        ConversationMessage retried = Assert.Single(stored, message => message.MessageId == "message-retry-1");
        Assert.Equal(2, retried.Sequence);
        Assert.Equal("idem-1", retried.IdempotencyKey);
        Assert.Equal(timestamp, retried.Timestamp);
        Assert.Equal("trace-42", retried.TraceId);
    }

    [Theory]
    [InlineData("user-2", ConversationType.User, false)]
    [InlineData("USER-1", ConversationType.User, false)]
    [InlineData("", ConversationType.User, false)]
    [InlineData("user-1", ConversationType.Channel, false)]
    [InlineData("user-1", ConversationType.User, true)]
    public async Task SaveAsync_InaccessibleConversation_RejectsBeforeAppendOrStatusChange(
        string userId, ConversationType type, bool deleted)
    {
        await using IConversationLockHandle handle = await AcquireLockAsync();
        InMemoryConversationStore store = new(new FakeUserContext());
        ConversationSessionStore sessions = new(store, Microsoft.Extensions.Options.Options.Create(new ConversationStoreOptions()));
        ConversationRecord record = new()
        {
            ConversationId = "conversation-1", TenantId = "tenant-1", UserId = "user-1",
            Version = 7, Status = ConversationStatus.Completed
        };
        Assert.True(await store.CreateAsync(record));
        record.IsDeletedByUser = deleted;
        ConversationContext context = new("conversation-1", "tenant-1", userId, "agent-1", "trace", type);

        AgentException error = await Assert.ThrowsAsync<AgentException>(() => sessions.SaveAsync(
            context, handle, 7, [Message(1, "user", "unauthorized")], ConversationStatus.Failed, CancellationToken.None));

        Assert.Equal(AgentErrorCode.PermissionDenied, error.ErrorCode);
        Assert.Equal(7, record.Version);
        Assert.Equal(ConversationStatus.Completed, record.Status);
        Assert.Empty(record.Messages);
    }

    [Fact]
    public async Task OpenAsync_ManualCompressionChangesGenerationWithoutChangingMessageVersion()
    {
        InMemoryConversationStore store = new(new FakeUserContext());
        ConversationSessionStore sessions = new(store, Microsoft.Extensions.Options.Options.Create(new ConversationStoreOptions()));
        ConversationContext context = new("conversation-1", "tenant-1", "user-1", "agent-1", "trace", ConversationType.User);
        ConversationSession before = await sessions.OpenAsync(context, "agent-1", "hello", CancellationToken.None);
        await store.RecordCompressionAsync("tenant-1", "conversation-1", new ContextSummary
        {
            CompressionId = "manual-1", Strategy = "summarization", Trigger = "Manual", Status = "Succeeded",
            Summary = "summary", SourceEndSequence = 1, CompactedMessages = [Message(1, "assistant", "summary")]
        });
        ConversationSession after = await sessions.OpenAsync(context, "agent-1", "continue", CancellationToken.None);
        Assert.Equal(before.CurrentVersion, after.CurrentVersion);
        Assert.NotEqual(before.ContextGeneration, after.ContextGeneration);
        Assert.Equal("manual-1", after.ContextGeneration);
    }

    private static async Task<IConversationLockHandle> AcquireLockAsync() =>
        Assert.IsAssignableFrom<IConversationLockHandle>(await new InMemoryConversationLock().TryAcquireAsync(
            "tenant-1", "conversation-1", TimeSpan.FromMinutes(1)));

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
