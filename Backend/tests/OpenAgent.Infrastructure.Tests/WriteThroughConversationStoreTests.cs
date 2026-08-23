using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

public sealed class WriteThroughConversationStoreTests
{
    [Fact]
    public async Task CreateAndAppend_WriteDurableRecordThenRefreshHotCache()
    {
        var durable = new FakeConversationStore();
        var cache = new FakeConversationCache();
        var store = new WriteThroughConversationStore(
            durable,
            cache,
            NullLogger<WriteThroughConversationStore>.Instance);
        ConversationRecord conversation = new()
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            AgentId = "agent-1",
            Version = 1
        };

        Assert.True(await store.CreateAsync(conversation));
        AppendResult append = await store.AppendMessagesAsync(
            "tenant-1",
            "conversation-1",
            1,
            [new ConversationMessage
            {
                MessageId = "message-1",
                Sequence = 1,
                Role = "user",
                Content = "hello"
            }]);

        Assert.True(append.Success);
        Assert.Equal(2, durable.Record.Version);
        Assert.Equal("hello", Assert.Single(cache.Record!.Messages).Content);
        Assert.Equal(2, cache.Record.Version);
    }

    [Fact]
    public async Task GetRecord_UsesHotCacheBeforeDurableStore()
    {
        var durable = new FakeConversationStore();
        var cache = new FakeConversationCache
        {
            Record = new ConversationRecord
            {
                ConversationId = "conversation-1",
                TenantId = "tenant-1",
                UserId = "user-1",
                AgentId = "agent-1"
            }
        };
        var store = new WriteThroughConversationStore(
            durable,
            cache,
            NullLogger<WriteThroughConversationStore>.Instance);

        ConversationRecord? record = await store.GetRecordAsync("tenant-1", "conversation-1");

        Assert.Same(cache.Record, record);
        Assert.Equal(0, durable.GetRecordCalls);
    }

    [Fact]
    public async Task UpdateModelOverride_WritesDurableRecordAndRefreshesHotCache()
    {
        var durable = new FakeConversationStore();
        var cache = new FakeConversationCache();
        var store = new WriteThroughConversationStore(
            durable,
            cache,
            NullLogger<WriteThroughConversationStore>.Instance);
        await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            Version = 1
        });

        bool updated = await store.UpdateModelOverrideAsync(
            "tenant-1",
            "conversation-1",
            new LlmModelSelection { Provider = "provider-1", ModelId = "model-1" },
            expectedVersion: 1);

        Assert.True(updated);
        Assert.Equal("model-1", durable.Record.ModelOverride?.ModelId);
        Assert.Equal("model-1", cache.Record?.ModelOverride?.ModelId);
        Assert.Equal(2, cache.Record?.Version);
    }

    private sealed class FakeConversationCache : IConversationCache
    {
        public ConversationRecord? Record { get; set; }

        public Task<ConversationRecord?> GetAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Record);

        public Task SetAsync(ConversationRecord record, CancellationToken cancellationToken = default)
        {
            Record = Clone(record);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default)
        {
            Record = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConversationStore : IConversationStore
    {
        public ConversationRecord Record { get; private set; } = new()
        {
            ConversationId = "uninitialized",
            TenantId = "uninitialized",
            UserId = "uninitialized"
        };
        public int GetRecordCalls { get; private set; }

        public Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default)
        {
            Record = Clone(record);
            return Task.FromResult(true);
        }

        public Task<AppendResult> AppendMessagesAsync(
            string tenantId, string conversationId, int expectedVersion, IReadOnlyList<ConversationMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Record.Messages.AddRange(messages);
            Record.Version += 1;
            Record.MessageCount = Record.Messages.Count;
            return Task.FromResult(AppendResult.Ok(Record.Version, Record.MessageCount));
        }

        public Task<ConversationRecord?> GetRecordAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default)
        {
            GetRecordCalls += 1;
            return Task.FromResult<ConversationRecord?>(Clone(Record));
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>(Record.Messages.TakeLast(maxMessages).ToArray());

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>(Record.Messages.Skip(skip).Take(take).ToArray());

        public Task<bool> UpdateStatusAsync(string tenantId, string conversationId, ConversationStatus status, int expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateModelOverrideAsync(
            string tenantId,
            string conversationId,
            LlmModelSelection? modelOverride,
            int expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Record.ModelOverride = modelOverride;
            Record.Version += 1;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(string tenantId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<bool> SoftDeleteAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RecordCompressionAsync(
            string tenantId,
            string conversationId,
            ContextSummary summary,
            CancellationToken cancellationToken = default)
        {
            Record.ContextSummaries.Add(summary);
            return Task.FromResult(true);
        }
    }

    private static ConversationRecord Clone(ConversationRecord record) => new()
    {
        ConversationId = record.ConversationId,
        TenantId = record.TenantId,
        UserId = record.UserId,
        AgentId = record.AgentId,
        ModelOverride = record.ModelOverride,
        Version = record.Version,
        MessageCount = record.MessageCount,
        Messages = record.Messages.Select(message => new ConversationMessage
        {
            MessageId = message.MessageId,
            Sequence = message.Sequence,
            Role = message.Role,
            Content = message.Content
        }).ToList(),
        ContextSummaries = record.ContextSummaries.ToList()
    };
}
