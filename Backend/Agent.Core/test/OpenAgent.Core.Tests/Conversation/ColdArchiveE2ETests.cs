using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Exten;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Conversation.Repository;
using StackExchange.Redis;
using Xunit;
using Xunit.Sdk;

namespace OpenAgent.Core.Tests.Conversation;

/// <summary>
/// E2E verification for cold archive logic via DualWriteConversationStore:
/// 1. Create conversation via DualWrite store (hot Redis + cold SQLite)
/// 2. Verify data in both hot and cold storage
/// 3. Simulate hot data expiry (delete from Redis)
/// 4. Verify DualWriteConversationStore falls back to cold archive on GetRecordAsync/GetMessagesAsync
/// 5. Verify warm-up: cold data is written back to hot store
/// Tests are skipped automatically when Redis at localhost:6379 is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public class ColdArchiveE2ETests : IAsyncLifetime
{
    private ServiceProvider? _serviceProvider;
    private IConversationStore? _store;
    private IConversationQueryService? _queryService;
    private IConversationRepository? _coldArchive;
    private IConnectionMultiplexer? _redis;
    private string? _dbPath;

    public async Task InitializeAsync()
    {
        // Probe Redis first; skip all tests in this class if unavailable.
        try
        {
            using var probe = await ConnectionMultiplexer.ConnectAsync(
                "localhost:6379,abortConnect=false,connectTimeout=500");
            if (!probe.IsConnected) throw new InvalidOperationException("Redis not connected");
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Redis unavailable at localhost:6379: {ex.Message}");
        }

        _dbPath = Path.Combine(Path.GetTempPath(), $"cold_archive_e2e_{Guid.NewGuid():N}.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = "localhost:6379",
                ["ConversationStore:EnableColdArchive"] = "true",
                ["ConversationStore:ColdArchiveProvider"] = "Sqlite",
                ["ConversationStore:ColdArchiveConnectionString"] = $"Data Source={_dbPath}",
                ["ConversationStore:RedisTtlMinutes"] = "30",
                ["ConversationStore:MaxHistoryMessages"] = "20",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAgentCore(config);
        _serviceProvider = services.BuildServiceProvider();

        _store = _serviceProvider.GetRequiredService<IConversationStore>();
        _queryService = _serviceProvider.GetRequiredService<IConversationQueryService>();
        _coldArchive = _serviceProvider.GetRequiredKeyedService<IConversationRepository>("Sqlite");
        _redis = _serviceProvider.GetRequiredService<IConnectionMultiplexer>();

        // Initialize cold archive tables
        await _coldArchive.EnsureInitializedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_serviceProvider != null) await _serviceProvider.DisposeAsync();
        if (_dbPath == null) return;

        // Allow fire-and-forget tasks (warm-up) to complete before deleting the SQLite file
        await Task.Delay(500);
        // Clean up SQLite file — best-effort, don't fail the test if file is still locked
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
                break;
            }
            catch (IOException) when (i < 9)
            {
                await Task.Delay(300);
            }
            catch (IOException)
            {
                // File still locked by fire-and-forget warm-up; temp file will be cleaned by OS
            }
        }
    }

    [SkippableFact]
    public async Task Step1_Create_conversation_writes_to_both_hot_and_cold()
    {
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var record = CreateRecord(conversationId, "tenant-e2e", "user-1");

        // Act: Create via DualWrite store
        var result = await _store!.CreateAsync(record);
        Assert.True(result, "CreateAsync should succeed");

        // Allow fire-and-forget cold archive to complete
        await Task.Delay(1000);

        // Assert: Hot store has the record
        var hotRecord = await _store.GetRecordAsync("tenant-e2e", conversationId);
        Assert.NotNull(hotRecord);
        Assert.Equal(conversationId, hotRecord!.ConversationId);

        // Assert: Cold archive has the record
        var coldRecord = await _coldArchive!.GetRecordAsync("tenant-e2e", conversationId);
        Assert.NotNull(coldRecord);
        Assert.Equal(conversationId, coldRecord!.ConversationId);

        Console.WriteLine($"[PASS] Step 1: Conversation {conversationId} exists in both hot and cold storage");
    }

    [SkippableFact]
    public async Task Step2_Append_messages_archives_to_cold()
    {
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var record = CreateRecord(conversationId, "tenant-e2e", "user-1");
        await _store!.CreateAsync(record);
        await Task.Delay(500);

        // Append messages
        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "Hello from E2E test" },
            new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "Hi there! How can I help?" }
        };

        var appendResult = await _store.AppendMessagesAsync("tenant-e2e", conversationId, 1, messages);
        Assert.True(appendResult.Success, $"Append should succeed, got: {appendResult.ConflictReason}");

        // Allow fire-and-forget cold archive to complete
        await Task.Delay(1000);

        // Assert: Cold archive has the messages
        var coldMessages = await _coldArchive!.LoadMessagesAsync("tenant-e2e", conversationId);
        Assert.Equal(2, coldMessages.Count);
        Assert.Equal("Hello from E2E test", coldMessages[0].Content);
        Assert.Equal("Hi there! How can I help?", coldMessages[1].Content);

        Console.WriteLine($"[PASS] Step 2: Messages archived to cold storage for {conversationId}");
    }

    [SkippableFact]
    public async Task Step3_GetRecordAsync_falls_back_to_cold_when_hot_expires()
    {
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var record = CreateRecord(conversationId, "tenant-e2e", "user-1");
        await _store!.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "Test cold fallback" },
        };
        await _store.AppendMessagesAsync("tenant-e2e", conversationId, 1, messages);

        // Wait for cold archive to complete
        await Task.Delay(3000);

        // Verify cold archive has the data before simulating expiry
        var coldCheck = await _coldArchive!.LoadMessagesAsync("tenant-e2e", conversationId);
        Console.WriteLine($"[DEBUG] Cold archive has {coldCheck.Count} messages before Redis deletion");

        // Verify hot store has the data
        var hotRecord = await _store.GetRecordAsync("tenant-e2e", conversationId);
        Assert.NotNull(hotRecord);

        // Simulate hot data expiry: delete from Redis
        var db = _redis!.GetDatabase();
        var key = $"conversation:tenant-e2e:{conversationId}";
        var deleted = await db.KeyDeleteAsync(key);
        Assert.True(deleted, "Should be able to delete the Redis key");

        // Also remove from tenant index
        var indexKey = $"conversation-index:tenant-e2e";
        await db.SetRemoveAsync(indexKey, conversationId);

        // Now query via IConversationStore.GetRecordAsync — should fall back to cold archive
        var queryResult = await _store.GetRecordAsync("tenant-e2e", conversationId);
        Assert.NotNull(queryResult);
        Assert.Equal(conversationId, queryResult!.ConversationId);

        // Cold archive should also load messages
        Assert.NotEmpty(queryResult.Messages);
        Assert.Equal("Test cold fallback", queryResult.Messages[0].Content);

        Console.WriteLine($"[PASS] Step 3: DualWriteConversationStore.GetRecordAsync falls back to cold archive when hot data expires");
    }

    [SkippableFact]
    public async Task Step4_WarmUp_writes_cold_data_back_to_hot_store()
    {
        var conversationId = $"conv-warmup-{Guid.NewGuid():N}";
        var record = CreateRecord(conversationId, "tenant-warmup", "user-1");
        await _store!.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "wm1", Sequence = 1, Role = "user", Content = "Warm-up test message" },
        };
        await _store.AppendMessagesAsync("tenant-warmup", conversationId, 1, messages);

        // Wait for cold archive to complete
        await Task.Delay(3000);

        // Simulate hot data expiry: delete from Redis
        var db = _redis!.GetDatabase();
        await db.KeyDeleteAsync($"conversation:tenant-warmup:{conversationId}");
        await db.SetRemoveAsync($"conversation-index:tenant-warmup", conversationId);

        // Verify hot store is empty
        var beforeWarmUp = await _store.GetRecordAsync("tenant-warmup", conversationId);
        // This should trigger cold archive fallback + warm-up
        Assert.NotNull(beforeWarmUp);

        // Wait for fire-and-forget warm-up to complete
        await Task.Delay(2000);

        // Verify data is now back in hot store (Redis)
        var redisKey = $"conversation:tenant-warmup:{conversationId}";
        var existsInRedis = await db.KeyExistsAsync(redisKey);
        Assert.True(existsInRedis, "Data should be warmed up back to Redis");

        Console.WriteLine($"[PASS] Step 4: Warm-up writes cold data back to hot store");
    }

    [SkippableFact]
    public async Task Step5_GetMessagesAsync_falls_back_to_cold()
    {
        var conversationId = $"conv-msgs-{Guid.NewGuid():N}";
        var record = CreateRecord(conversationId, "tenant-msgs", "user-1");
        await _store!.CreateAsync(record);

        // Append 10 messages
        var messages = Enumerable.Range(1, 10)
            .Select(i => new ConversationMessage
            {
                MessageId = $"m{i}",
                Sequence = i,
                Role = i % 2 == 0 ? "assistant" : "user",
                Content = $"message-{i}"
            })
            .ToList();
        await _store.AppendMessagesAsync("tenant-msgs", conversationId, 1, messages);
        await Task.Delay(1500);

        // Delete from Redis
        var db = _redis!.GetDatabase();
        await db.KeyDeleteAsync($"conversation:tenant-msgs:{conversationId}");
        await db.SetRemoveAsync($"conversation-index:tenant-msgs", conversationId);

        // Query messages via IConversationStore — should fall back to cold archive
        var result = await _store.GetMessagesAsync("tenant-msgs", conversationId, 20);
        Assert.Equal(10, result.Count);
        Assert.Equal("message-1", result[0].Content);
        Assert.Equal("message-10", result[9].Content);

        Console.WriteLine($"[PASS] Step 5: GetMessagesAsync falls back to cold archive");
    }

    [SkippableFact]
    public async Task Step6_QueryService_merges_hot_and_cold_results()
    {
        // Create two conversations
        var conv1 = $"conv-merge-{Guid.NewGuid():N}";
        var conv2 = $"conv-merge-{Guid.NewGuid():N}";

        var record1 = CreateRecord(conv1, "tenant-merge", "user-1");
        record1.LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _store!.CreateAsync(record1);

        var record2 = CreateRecord(conv2, "tenant-merge", "user-1");
        record2.LastMessageAt = DateTimeOffset.UtcNow;
        await _store.CreateAsync(record2);

        // Append messages to both
        await _store.AppendMessagesAsync("tenant-merge", conv1, 1, new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "message in conv1" }
        });
        await _store.AppendMessagesAsync("tenant-merge", conv2, 1, new List<ConversationMessage>
        {
            new() { MessageId = "m2", Sequence = 1, Role = "user", Content = "message in conv2" }
        });

        await Task.Delay(1500);

        // Delete conv1 from Redis (simulate expiry)
        var db = _redis!.GetDatabase();
        await db.KeyDeleteAsync($"conversation:tenant-merge:{conv1}");
        await db.SetRemoveAsync($"conversation-index:tenant-merge", conv1);

        // conv2 still in Redis, conv1 only in cold archive
        var results = await _queryService!.ListConversationsAsync("tenant-merge", 0, 10);
        Assert.True(results.Count >= 2, $"Expected at least 2 results, got {results.Count}");

        // Both should be present (conv2 from hot, conv1 from cold)
        var ids = results.Select(r => r.ConversationId).ToList();
        Assert.Contains(conv1, ids);
        Assert.Contains(conv2, ids);

        Console.WriteLine($"[PASS] Step 6: QueryService merges hot + cold results correctly");
    }

    private static ConversationRecord CreateRecord(string conversationId, string tenantId, string userId) => new()
    {
        ConversationId = conversationId,
        TenantId = tenantId,
        UserId = userId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        LastMessageAt = DateTimeOffset.UtcNow
    };
}
