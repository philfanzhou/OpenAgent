using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public sealed class AuditedCompactionStrategyTests
{
    [Fact]
    public async Task CompactAsync_TriggerFires_RecordsSuccessfulAutomaticCompression()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 6);
        SummarizationCompactionStrategy strategy = SummaryStrategy("compressed summary");
        var audited = CreateAudited(
            store,
            strategy,
            recordUnchanged: false,
            CompactionTriggers.MessagesExceed(2));

        IEnumerable<ChatMessage> result = await CompactionProvider.CompactAsync(
            audited,
            Messages(6),
            NullLogger.Instance,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await store.GetRecordAsync("tenant-1", "conversation-1"));
        ContextSummary summary = Assert.Single(record.ContextSummaries);

        Assert.True(result.Count() < 6);
        Assert.Equal("Succeeded", summary.Status);
        Assert.Equal("Automatic", summary.Trigger);
        Assert.Equal("summarization", summary.Strategy);
        Assert.True(summary.CompressedMessageCount > 0);
        Assert.True(summary.OriginalTokenCount > summary.TokenCount);
        Assert.False(string.IsNullOrWhiteSpace(summary.Summary));
    }

    [Fact]
    public async Task CompactAsync_TriggerDoesNotFire_DoesNotRecordCompression()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 2);
        SummarizationCompactionStrategy strategy = SummaryStrategy("not called");
        var audited = CreateAudited(
            store,
            strategy,
            recordUnchanged: false,
            CompactionTriggers.MessagesExceed(20));

        IEnumerable<ChatMessage> result = await CompactionProvider.CompactAsync(
            audited,
            Messages(2),
            NullLogger.Instance,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await store.GetRecordAsync("tenant-1", "conversation-1"));

        Assert.Equal(2, result.Count());
        Assert.Empty(record.ContextSummaries);
    }

    [Fact]
    public async Task CompactAsync_SummarizationClientFails_RestoresHistoryAndRecordsFailure()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 4);
        var strategy = new SummarizationCompactionStrategy(
            new FakeChatProvider(new InvalidOperationException("summary model unavailable")),
            CompactionTriggers.Always,
            minimumPreservedGroups: 1,
            summarizationPrompt: null,
            target: null);
        var audited = CreateAudited(store, strategy, recordUnchanged: false);
        List<ChatMessage> original = Messages(4);

        IEnumerable<ChatMessage> result = await CompactionProvider.CompactAsync(
            audited,
            original,
            NullLogger.Instance,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await store.GetRecordAsync("tenant-1", "conversation-1"));
        ContextSummary summary = Assert.Single(record.ContextSummaries);

        Assert.Equal(original, result);
        Assert.Equal("Failed", summary.Status);
        Assert.True(summary.OriginalHistoryRestored);
    }

    [Fact]
    public async Task CompactAsync_UnavailableSummaryPlaceholder_RestoresHistoryAndRecordsFailure()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 4);
        var strategy = new SummarizationCompactionStrategy(
            new FakeChatProvider(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "[Summary unavailable]"))),
            CompactionTriggers.Always,
            minimumPreservedGroups: 1,
            summarizationPrompt: null,
            target: null);
        var audited = new AuditedCompactionStrategy(
            strategy,
            CompactionTriggers.Always,
            "Manual",
            "tenant-1",
            "conversation-1",
            store,
            NullLogger<AuditedCompactionStrategy>.Instance,
            recordUnchanged: true);
        List<ChatMessage> original = Messages(4);

        IEnumerable<ChatMessage> result = await CompactionProvider.CompactAsync(
            audited,
            original,
            NullLogger.Instance,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await store.GetRecordAsync("tenant-1", "conversation-1"));
        ContextSummary summary = Assert.Single(record.ContextSummaries);

        Assert.Equal(original, result);
        Assert.Equal("Failed", summary.Status);
        Assert.True(summary.OriginalHistoryRestored);
        Assert.Contains("usable summary", summary.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompactAsync_ResultDoesNotSaveTenPercent_RestoresHistoryAndRecordsSkippedAttempt()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 4);
        string oversizedSummary = new('x', 4_000);
        var strategy = new SummarizationCompactionStrategy(
            new FakeChatProvider(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, oversizedSummary))),
            CompactionTriggers.Always,
            minimumPreservedGroups: 1,
            summarizationPrompt: null,
            target: null);
        var audited = CreateAudited(store, strategy, recordUnchanged: false);
        List<ChatMessage> original = Messages(4);

        IEnumerable<ChatMessage> result = await CompactionProvider.CompactAsync(
            audited,
            original,
            NullLogger.Instance,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await store.GetRecordAsync("tenant-1", "conversation-1"));
        ContextSummary summary = Assert.Single(record.ContextSummaries);

        Assert.Equal(original, result);
        Assert.Equal("Skipped", summary.Status);
        Assert.True(summary.OriginalHistoryRestored);
        Assert.Equal(summary.OriginalTokenCount, summary.TokenCount);
        Assert.Contains("rejected", summary.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(summary.CompactedMessages);
    }

    [Fact]
    public async Task CompactAsync_ManualRequestBelowBudget_RecordsSkippedAttemptWithoutCallingStrategy()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 2);
        var audited = CreateAudited(
            store,
            SummaryStrategy("not called"),
            recordUnchanged: true,
            CompactionTriggers.MessagesExceed(20));

        IEnumerable<ChatMessage> result = await CompactionProvider.CompactAsync(
            audited,
            Messages(2),
            NullLogger.Instance,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await store.GetRecordAsync("tenant-1", "conversation-1"));
        ContextSummary summary = Assert.Single(record.ContextSummaries);

        Assert.Equal(2, result.Count());
        Assert.Equal("Skipped", summary.Status);
        Assert.False(summary.OriginalHistoryRestored);
        Assert.Contains("within the compaction target", summary.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordNotRunAsync_IncompleteHistory_PersistsStableAudit()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 1);
        var audited = CreateAudited(
            store,
            SummaryStrategy("not called"),
            recordUnchanged: true);

        await audited.RecordNotRunAsync(
            [new ChatMessage(ChatRole.User, "incomplete user-only history")]);

        ContextSummary summary = Assert.Single(Assert.IsType<ConversationRecord>(
            await store.GetRecordAsync("tenant-1", "conversation-1")).ContextSummaries);
        Assert.Equal("Skipped", summary.Status);
        Assert.True(summary.OriginalTokenCount > 0);
        Assert.Equal(summary.OriginalTokenCount, summary.TokenCount);
        Assert.Contains("completed message group", summary.Result, StringComparison.OrdinalIgnoreCase);
        Assert.True(audited.LastAuditRecorded);
    }

    private static AuditedCompactionStrategy CreateAudited(
        IConversationStore store,
        SummarizationCompactionStrategy strategy,
        bool recordUnchanged,
        CompactionTrigger? triggerCondition = null) => new(
            strategy,
            triggerCondition ?? CompactionTriggers.Always,
            "Automatic",
            "tenant-1",
            "conversation-1",
            store,
            NullLogger<AuditedCompactionStrategy>.Instance,
            recordUnchanged);

    private static async Task<InMemoryConversationStore> CreateStoreAsync(int messageCount)
    {
        var store = new InMemoryConversationStore(new UserContext());
        await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            AgentId = "agent-1",
            MessageCount = messageCount
        });
        return store;
    }

    private static List<ChatMessage> Messages(int count) => Enumerable.Range(1, count)
        .Select(index => new ChatMessage(
            index % 2 == 0 ? ChatRole.Assistant : ChatRole.User,
            $"message-{index}"))
        .ToList();

    private static SummarizationCompactionStrategy SummaryStrategy(string summary) => new(
        new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, summary))),
        CompactionTriggers.Always,
        minimumPreservedGroups: 1,
        summarizationPrompt: null,
        target: null);

    private sealed class UserContext : ICurrentUserContext
    {
        public string UserId => "user-1";
        public string? TenantId => "tenant-1";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }
}
