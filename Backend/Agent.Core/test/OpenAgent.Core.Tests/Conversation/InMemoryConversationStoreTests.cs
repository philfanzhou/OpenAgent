using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Store;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class InMemoryConversationStoreTests
{
    [Fact]
    public async Task CreateAsync_creates_record_successfully()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");

        var result = await store.CreateAsync(record);

        Assert.True(result);
        var fetched = await store.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal("conv-1", fetched!.ConversationId);
        Assert.Equal("tenant-1", fetched.TenantId);
        Assert.Equal("user-1", fetched.UserId);
    }

    [Fact]
    public async Task CreateAsync_returns_false_when_duplicate()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        var result = await store.CreateAsync(record);

        Assert.False(result);
    }

    [Fact]
    public async Task GetMessagesAsync_returns_empty_when_not_found()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);

        var messages = await store.GetMessagesAsync("tenant-1", "nonexistent", 10);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task GetRecordAsync_returns_null_when_not_found()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);

        var record = await store.GetRecordAsync("tenant-1", "nonexistent");

        Assert.Null(record);
    }

    [Fact]
    public async Task AppendMessagesAsync_appends_and_increments_version()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" },
            new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "hi there" }
        };

        var result = await store.AppendMessagesAsync("tenant-1", "conv-1", 1, messages);

        Assert.True(result.Success);
        Assert.Equal(2, result.NewVersion);
        Assert.Equal(2, result.NewMessageCount);

        var fetched = await store.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.Messages.Count);
        Assert.Equal(2, fetched.Version);
        Assert.Equal("hello", fetched.Messages[0].Content);
        Assert.Equal("hi there", fetched.Messages[1].Content);
    }

    [Fact]
    public async Task AppendMessagesAsync_fails_on_version_conflict()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" }
        };

        var result = await store.AppendMessagesAsync("tenant-1", "conv-1", 999, messages);

        Assert.False(result.Success);
        Assert.NotNull(result.ConflictReason);
        Assert.Contains("Version conflict", result.ConflictReason);
    }

    [Fact]
    public async Task AppendMessagesAsync_fails_when_conversation_not_found()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" }
        };

        var result = await store.AppendMessagesAsync("tenant-1", "nonexistent", 1, messages);

        Assert.False(result.Success);
        Assert.Equal("Conversation not found", result.ConflictReason);
    }

    [Fact]
    public async Task AppendMessagesAsync_concurrent_appends_are_atomic()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        const int taskCount = 100;
        var tasks = new List<Task<AppendResult>>();
        for (int i = 0; i < taskCount; i++)
        {
            var version = 1 + i;
            var messages = new List<ConversationMessage>
            {
                new() { MessageId = $"m-{i}", Sequence = i + 1, Role = "user", Content = $"hello-{i}" }
            };
            tasks.Add(store.AppendMessagesAsync("tenant-1", "conv-1", version, messages));
        }

        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.Success);
        var conflictReasons = string.Join("; ", results.Where(r => !r.Success).Select(r => r.ConflictReason));
        Assert.True(successCount == taskCount, $"Expected {taskCount} successes but got {successCount}. Conflicts: {conflictReasons}");

        var fetched = await store.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.True(fetched!.Version == taskCount + 1,
            $"Expected version {taskCount + 1} but got {fetched.Version}. Initial version was {record.Version}. Message count {fetched.Messages.Count}. Success count {successCount}.");
        Assert.Equal(taskCount, fetched.Messages.Count);
    }

    [Fact]
    public async Task AppendMessagesAsync_concurrent_same_expected_version_one_succeeds()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        const int taskCount = 10;
        var tasks = new List<Task<AppendResult>>();
        for (int i = 0; i < taskCount; i++)
        {
            var messages = new List<ConversationMessage>
            {
                new() { MessageId = $"m-{i}", Sequence = i + 1, Role = "user", Content = $"hello-{i}" }
            };
            tasks.Add(store.AppendMessagesAsync("tenant-1", "conv-1", expectedVersion: 1, messages));
        }

        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.Success);
        Assert.Equal(1, successCount);

        var fetched = await store.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.Version);
    }

    [Fact]
    public async Task UpdateStatusAsync_updates_status_and_increments_version()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        var result = await store.UpdateStatusAsync("tenant-1", "conv-1", ConversationStatus.Completed, 1);

        Assert.True(result);

        var fetched = await store.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal(ConversationStatus.Completed, fetched!.Status);
        Assert.Equal(2, fetched.Version);
    }

    [Fact]
    public async Task UpdateStatusAsync_fails_on_version_conflict()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        var result = await store.UpdateStatusAsync("tenant-1", "conv-1", ConversationStatus.Completed, 999);

        Assert.False(result);

        var fetched = await store.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal(ConversationStatus.Running, fetched!.Status);
        Assert.Equal(1, fetched.Version);
    }

    [Fact]
    public async Task Tenant_isolation_different_tenants_cannot_access_same_conversation()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var recordA = CreateRecord("conv-1", "tenant-A", "user-1");
        await store.CreateAsync(recordA);

        // tenant-B cannot see tenant-A's conversation
        var fetchedByB = await store.GetRecordAsync("tenant-B", "conv-1");
        Assert.Null(fetchedByB);

        // tenant-B can create a conversation with the same ID
        var recordB = CreateRecord("conv-1", "tenant-B", "user-2");
        var createResult = await store.CreateAsync(recordB);
        Assert.True(createResult);

        // Both tenants have their own independent records
        var fetchedA = await store.GetRecordAsync("tenant-A", "conv-1");
        var fetchedB = await store.GetRecordAsync("tenant-B", "conv-1");
        Assert.NotNull(fetchedA);
        Assert.NotNull(fetchedB);
        Assert.Equal("tenant-A", fetchedA!.TenantId);
        Assert.Equal("tenant-B", fetchedB!.TenantId);

        // Appending to tenant-A does not affect tenant-B
        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello-A" }
        };
        await store.AppendMessagesAsync("tenant-A", "conv-1", 1, messages);

        fetchedA = await store.GetRecordAsync("tenant-A", "conv-1");
        fetchedB = await store.GetRecordAsync("tenant-B", "conv-1");
        Assert.Equal(2, fetchedA!.Version);
        Assert.Equal(1, fetchedB!.Version);
    }

    [Fact]
    public async Task GetMessagesAsync_respects_maxMessages_limit()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");

        var allMessages = Enumerable.Range(1, 5)
            .Select(i => new ConversationMessage
            {
                MessageId = $"m{i}",
                Sequence = i,
                Role = i % 2 == 0 ? "assistant" : "user",
                Content = $"message-{i}"
            })
            .ToList();
        record.Messages = allMessages;
        record.MessageCount = 5;
        await store.CreateAsync(record);

        // Request only the last 3 messages
        var result = await store.GetMessagesAsync("tenant-1", "conv-1", 3);

        Assert.Equal(3, result.Count);
        Assert.Equal("message-3", result[0].Content);
        Assert.Equal("message-4", result[1].Content);
        Assert.Equal("message-5", result[2].Content);
    }

    [Fact]
    public async Task ListConversationsAsync_returns_conversations_for_tenant()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);

        // Create 3 conversations for tenant-1 with distinct LastMessageAt
        var record1 = CreateRecord("conv-1", "tenant-1", "user-1");
        record1.LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        await store.CreateAsync(record1);

        var record2 = CreateRecord("conv-2", "tenant-1", "user-1");
        record2.LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.CreateAsync(record2);

        var record3 = CreateRecord("conv-3", "tenant-1", "user-1");
        record3.LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await store.CreateAsync(record3);

        // Create 1 conversation for tenant-2
        var recordOther = CreateRecord("conv-4", "tenant-2", "user-2");
        await store.CreateAsync(recordOther);

        var result = await store.ListConversationsAsync("tenant-1", skip: 0, take: 2);

        Assert.Equal(2, result.Count);
        // Ordered by LastMessageAt desc: conv-2 (most recent), conv-3, conv-1 (oldest)
        Assert.Equal("conv-2", result[0].ConversationId);
        Assert.Equal("conv-3", result[1].ConversationId);
    }

    [Fact]
    public async Task ListConversationsAsync_returns_empty_for_unknown_tenant()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        var result = await store.ListConversationsAsync("nonexistent-tenant", skip: 0, take: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListConversationsAsync_respects_paging()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);

        // Create 5 conversations with distinct LastMessageAt
        for (int i = 1; i <= 5; i++)
        {
            var record = CreateRecord($"conv-{i}", "tenant-1", "user-1");
            record.LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-6 + i); // conv-5 newest, conv-1 oldest
            await store.CreateAsync(record);
        }

        // Skip first 2 (conv-5, conv-4), take 2 (conv-3, conv-2)
        var result = await store.ListConversationsAsync("tenant-1", skip: 2, take: 2);

        Assert.Equal(2, result.Count);
        Assert.Equal("conv-3", result[0].ConversationId);
        Assert.Equal("conv-2", result[1].ConversationId);
    }

    [Fact]
    public async Task SearchConversationsAsync_finds_matching_content()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        record.Messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello world" }
        };
        record.MessageCount = 1;
        await store.CreateAsync(record);

        var result = await store.SearchConversationsAsync("tenant-1", "hello", skip: 0, take: 10);

        Assert.Single(result);
        Assert.Equal("conv-1", result[0].ConversationId);
    }

    [Fact]
    public async Task SearchConversationsAsync_case_insensitive()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        record.Messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "Hello World" }
        };
        record.MessageCount = 1;
        await store.CreateAsync(record);

        var result = await store.SearchConversationsAsync("tenant-1", "hello", skip: 0, take: 10);

        Assert.Single(result);
        Assert.Equal("conv-1", result[0].ConversationId);
    }

    [Fact]
    public async Task SearchConversationsAsync_no_match()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        record.Messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello world" }
        };
        record.MessageCount = 1;
        await store.CreateAsync(record);

        var result = await store.SearchConversationsAsync("tenant-1", "nonexistent", skip: 0, take: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMessagesPagedAsync_returns_paged_messages()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        record.Messages = Enumerable.Range(1, 10)
            .Select(i => new ConversationMessage
            {
                MessageId = $"m{i}",
                Sequence = i,
                Role = i % 2 == 0 ? "assistant" : "user",
                Content = $"message-{i}"
            })
            .ToList();
        record.MessageCount = 10;
        await store.CreateAsync(record);

        var result = await store.GetMessagesPagedAsync("tenant-1", "conv-1", skip: 2, take: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal("message-3", result[0].Content);
        Assert.Equal("message-4", result[1].Content);
        Assert.Equal("message-5", result[2].Content);
    }

    [Fact]
    public async Task GetMessagesPagedAsync_returns_empty_for_nonexistent()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);

        var result = await store.GetMessagesPagedAsync("tenant-1", "nonexistent", skip: 0, take: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AppendMessagesAsync_idempotency_key_dedup()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await store.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello", IdempotencyKey = "key-1" },
            new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "hi", IdempotencyKey = "key-2" }
        };

        var first = await store.AppendMessagesAsync("tenant-1", "conv-1", expectedVersion: 1, messages);
        Assert.True(first.Success);
        Assert.Equal(0, first.SkippedDuplicateCount);

        // Append the same messages again (same IdempotencyKeys)
        var second = await store.AppendMessagesAsync("tenant-1", "conv-1", expectedVersion: 2, messages);
        Assert.True(second.Success);
        Assert.True(second.SkippedDuplicateCount > 0);

        // Verify messages are not duplicated
        var fetched = await store.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.Messages.Count);
    }

    private static ConversationRecord CreateRecord(string conversationId, string tenantId, string userId) => new()
    {
        ConversationId = conversationId,
        TenantId = tenantId,
        UserId = userId
    };
}
