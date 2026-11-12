using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Framework.Tests;

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
            new FakeUserContext(),
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
                AgentId = "agent-1",
                Messages =
                [
                    new ConversationMessage { MessageId = "message-1", Sequence = 1, Role = "user", Content = "first" },
                    new ConversationMessage { MessageId = "message-2", Sequence = 2, Role = "assistant", Content = "last" }
                ]
            }
        };
        var store = new WriteThroughConversationStore(
            durable,
            cache,
            new FakeUserContext(),
            NullLogger<WriteThroughConversationStore>.Instance);

        ConversationRecord? record = await store.GetRecordAsync("tenant-1", "conversation-1");

        Assert.Same(cache.Record, record);
        Assert.Equal("last", Assert.Single(await store.GetMessagesAsync("tenant-1", "conversation-1", 1)).Content);
        Assert.Equal("first", Assert.Single(await store.GetMessagesPagedAsync("tenant-1", "conversation-1", 0, 1)).Content);
        Assert.Equal(0, durable.GetRecordCalls);
    }

    [Theory]
    [InlineData("user-2", "tenant-1", "conversation-1", false)]
    [InlineData("USER-1", "tenant-1", "conversation-1", false)]
    [InlineData(null, "tenant-1", "conversation-1", false)]
    [InlineData("", "tenant-1", "conversation-1", false)]
    [InlineData("user-1", "tenant-2", "conversation-1", false)]
    [InlineData("user-1", "tenant-1", "conversation-2", false)]
    [InlineData("user-1", "tenant-1", "conversation-1", true)]
    public async Task Reads_InvalidCachedRecord_FallBackWithoutExposingCachedMessages(
        string? userId, string tenantId, string conversationId, bool deleted)
    {
        FakeConversationStore durable = new() { AllowRead = false };
        FakeConversationCache cache = new()
        {
            Record = new ConversationRecord
            {
                ConversationId = conversationId,
                TenantId = tenantId,
                UserId = userId!,
                IsDeletedByUser = deleted,
                Messages = [new ConversationMessage { MessageId = "secret", Sequence = 1, Role = "user", Content = "private" }]
            }
        };
        WriteThroughConversationStore store = new(
            durable, cache, new FakeUserContext(), NullLogger<WriteThroughConversationStore>.Instance);

        Assert.Null(await store.GetRecordAsync("tenant-1", "conversation-1"));
        Assert.Empty(await store.GetMessagesAsync("tenant-1", "conversation-1", 10));
        Assert.Empty(await store.GetMessagesPagedAsync("tenant-1", "conversation-1", 0, 10));
        Assert.Equal(3, durable.GetRecordCalls);
        Assert.Equal(0, cache.SetCalls);
    }

    [Theory]
    [InlineData("user-2", "tenant-1", true)]
    [InlineData("USER-1", "tenant-1", true)]
    [InlineData(null, "tenant-1", true)]
    [InlineData("", "tenant-1", true)]
    [InlineData(" ", "tenant-1", true)]
    [InlineData("user-1", "tenant-2", true)]
    [InlineData("user-1", "TENANT-1", true)]
    [InlineData("user-1", null, true)]
    [InlineData("user-1", "", true)]
    [InlineData("user-1", " ", true)]
    [InlineData("user-1", "tenant-1", false)]
    public async Task Reads_UntrustedOrForeignIdentity_DoNotUseOwnersHotCache(
        string? userId, string? tenantId, bool authenticated)
    {
        FakeConversationStore durable = new() { AllowRead = false };
        FakeConversationCache cache = new()
        {
            Record = new ConversationRecord
            {
                ConversationId = "conversation-1",
                TenantId = "tenant-1",
                UserId = "user-1",
                Messages = [new ConversationMessage { MessageId = "secret", Sequence = 1, Role = "user", Content = "private" }]
            }
        };
        FakeUserContext identity = new() { UserId = userId!, TenantId = tenantId, IsAuthenticated = authenticated };
        WriteThroughConversationStore store = new(
            durable, cache, identity, NullLogger<WriteThroughConversationStore>.Instance);

        Assert.Null(await store.GetRecordAsync("tenant-1", "conversation-1"));
        Assert.Empty(await store.GetMessagesAsync("tenant-1", "conversation-1", 10));
        Assert.Empty(await store.GetMessagesPagedAsync("tenant-1", "conversation-1", 0, 10));
        Assert.Equal(0, cache.SetCalls);
    }

    [Fact]
    public async Task Reads_CacheFailure_RetainDurableAuthorizationDenial()
    {
        FakeConversationStore durable = new() { AllowRead = false };
        FakeConversationCache cache = new() { ThrowOnGet = true };
        WriteThroughConversationStore store = new(
            durable, cache, new FakeUserContext(), NullLogger<WriteThroughConversationStore>.Instance);

        Assert.Null(await store.GetRecordAsync("tenant-1", "conversation-1"));
        Assert.Empty(await store.GetMessagesAsync("tenant-1", "conversation-1", 10));
        Assert.Empty(await store.GetMessagesPagedAsync("tenant-1", "conversation-1", 0, 10));
        Assert.Equal(0, cache.SetCalls);
    }

    [Fact]
    public async Task Mutations_DurableRejection_DoNotWarmReplaceOrRemoveHotCache()
    {
        FakeConversationStore durable = new() { AllowWrite = false };
        FakeConversationCache cache = new();
        WriteThroughConversationStore store = new(
            durable, cache, new FakeUserContext(), NullLogger<WriteThroughConversationStore>.Instance);

        Assert.False(await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-1", TenantId = "tenant-1", UserId = "user-1"
        }));
        Assert.False((await store.AppendMessagesAsync("tenant-1", "conversation-1", 1, [])).Success);
        foreach (ConversationStatus status in Enum.GetValues<ConversationStatus>())
        {
            Assert.False(await store.UpdateStatusAsync("tenant-1", "conversation-1", status, 1));
        }
        Assert.False(await store.SoftDeleteAsync("tenant-1", "conversation-1"));
        Assert.False(await store.RecordCompressionAsync("tenant-1", "conversation-1", new ContextSummary
        {
            CompressionId = "unauthorized", Strategy = "test", Trigger = "Manual", Status = "Succeeded"
        }));

        Assert.Equal(0, durable.GetRecordCalls);
        Assert.Equal(0, cache.SetCalls);
        Assert.Equal(0, cache.RemoveCalls);
    }

    private sealed class FakeUserContext : ICurrentUserContext
    {
        public string UserId { get; init; } = "user-1";
        public string? TenantId { get; init; } = "tenant-1";
        public bool IsAuthenticated { get; init; } = true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }

    private sealed class FakeConversationCache : IConversationCache
    {
        public ConversationRecord? Record { get; set; }
        public bool ThrowOnGet { get; init; }
        public int SetCalls { get; private set; }
        public int RemoveCalls { get; private set; }

        public Task<ConversationRecord?> GetAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            ThrowOnGet ? throw new InvalidOperationException("Cache unavailable") : Task.FromResult(Record);

        public Task SetAsync(ConversationRecord record, CancellationToken cancellationToken = default)
        {
            Record = Clone(record);
            SetCalls += 1;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default)
        {
            Record = null;
            RemoveCalls += 1;
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
        public bool AllowRead { get; init; } = true;
        public bool AllowWrite { get; init; } = true;

        public Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default)
        {
            if (!AllowWrite)
            {
                return Task.FromResult(false);
            }
            Record = Clone(record);
            return Task.FromResult(true);
        }

        public Task<AppendResult> AppendMessagesAsync(
            string tenantId, string conversationId, int expectedVersion, IReadOnlyList<ConversationMessage> messages,
            CancellationToken cancellationToken = default)
        {
            if (!AllowWrite)
            {
                return Task.FromResult(AppendResult.Conflict("conversation-not-found"));
            }
            Record.Messages.AddRange(messages);
            Record.Version += 1;
            Record.MessageCount = Record.Messages.Count;
            return Task.FromResult(AppendResult.Ok(Record.Version, Record.MessageCount));
        }

        public Task<ConversationRecord?> GetRecordAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default)
        {
            GetRecordCalls += 1;
            return Task.FromResult<ConversationRecord?>(AllowRead ? Clone(Record) : null);
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>(AllowRead ? Record.Messages.TakeLast(maxMessages).ToArray() : []);

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>(AllowRead ? Record.Messages.Skip(skip).Take(take).ToArray() : []);

        public Task<bool> UpdateStatusAsync(string tenantId, string conversationId, ConversationStatus status, int expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(AllowWrite);

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(string tenantId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<bool> SoftDeleteAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AllowWrite);

        public Task<bool> RecordCompressionAsync(
            string tenantId,
            string conversationId,
            ContextSummary summary,
            CancellationToken cancellationToken = default)
        {
            if (!AllowWrite)
            {
                return Task.FromResult(false);
            }
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
        Version = record.Version,
        IsDeletedByUser = record.IsDeletedByUser,
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
