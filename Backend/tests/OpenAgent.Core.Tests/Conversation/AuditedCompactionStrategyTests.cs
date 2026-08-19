using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
        var strategy = new TruncationCompactionStrategy(
            CompactionTriggers.MessagesExceed(2),
            minimumPreservedGroups: 1,
            target: null);
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
        Assert.Equal("truncation", summary.Strategy);
        Assert.True(summary.CompressedMessageCount > 0);
    }

    [Fact]
    public async Task CompactAsync_TriggerDoesNotFire_DoesNotRecordCompression()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 2);
        var strategy = new TruncationCompactionStrategy(
            CompactionTriggers.MessagesExceed(20),
            minimumPreservedGroups: 1,
            target: null);
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
    public async Task CompactAsync_StrategyFails_RestoresHistoryAndRecordsFailure()
    {
        InMemoryConversationStore store = await CreateStoreAsync(messageCount: 4);
        var audited = CreateAudited(store, new MutatingFailureStrategy(), recordUnchanged: false);
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
        Assert.Equal("Original history restored for model invocation.", summary.Result);
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

    private static AuditedCompactionStrategy CreateAudited(
        IConversationStore store,
        CompactionStrategy strategy,
        bool recordUnchanged,
        CompactionTrigger? triggerCondition = null) => new(
            strategy,
            triggerCondition ?? CompactionTriggers.Always,
            "truncation",
            "Automatic",
            new ConversationContext("conversation-1", "tenant-1", "user-1", "agent-1", "trace-1"),
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

    private sealed class MutatingFailureStrategy()
        : CompactionStrategy(CompactionTriggers.Always, target: null)
    {
        protected override ValueTask<bool> CompactCoreAsync(
            CompactionMessageIndex index,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            index.Groups[0].IsExcluded = true;
            throw new InvalidOperationException("summarization failed");
        }
    }

    private sealed class UserContext : ICurrentUserContext
    {
        public string UserId => "user-1";
        public string? TenantId => "tenant-1";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }
}
