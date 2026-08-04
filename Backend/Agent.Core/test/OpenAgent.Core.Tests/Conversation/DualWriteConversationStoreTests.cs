using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Conversation.Repository;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class DualWriteConversationStoreTests
{
    [Fact]
    public async Task CreateAsync_delegates_to_hot_store()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        var result = await dualStore.CreateAsync(record);

        Assert.True(result);
        var fetched = await hotStore.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal("conv-1", fetched!.ConversationId);
    }

    [Fact]
    public async Task CreateAsync_skips_cold_archive_when_disabled()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        var result = await dualStore.CreateAsync(record);

        Assert.True(result);
    }

    [Fact]
    public async Task AppendMessagesAsync_delegates_to_hot_store()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await dualStore.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" }
        };

        var result = await dualStore.AppendMessagesAsync("tenant-1", "conv-1", 1, messages);

        Assert.True(result.Success);
        Assert.Equal(2, result.NewVersion);

        var fetched = await hotStore.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Single(fetched!.Messages);
        Assert.Equal("hello", fetched.Messages[0].Content);
    }

    [Fact]
    public async Task UpdateStatusAsync_delegates_to_hot_store()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await dualStore.CreateAsync(record);

        var result = await dualStore.UpdateStatusAsync("tenant-1", "conv-1", ConversationStatus.Failed, 1);

        Assert.True(result);
        var fetched = await hotStore.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal(ConversationStatus.Failed, fetched!.Status);
        Assert.Equal(2, fetched.Version);
    }

    [Fact]
    public async Task GetMessagesAsync_delegates_to_hot_store()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        record.Messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" },
            new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "world" }
        };
        await hotStore.CreateAsync(record);

        var messages = await dualStore.GetMessagesAsync("tenant-1", "conv-1", 10);

        Assert.Equal(2, messages.Count);
        Assert.Equal("hello", messages[0].Content);
        Assert.Equal("world", messages[1].Content);
    }

    [Fact]
    public async Task GetRecordAsync_delegates_to_hot_store()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        record.AgentId = "agent-x";
        await hotStore.CreateAsync(record);

        var fetched = await dualStore.GetRecordAsync("tenant-1", "conv-1");

        Assert.NotNull(fetched);
        Assert.Equal("conv-1", fetched!.ConversationId);
        Assert.Equal("agent-x", fetched.AgentId);
    }


    #region AppendMessagesAsync with message-level archive compensation

    [Fact]
    public async Task AppendMessagesAsync_with_cold_archive_enabled_does_not_crash()
    {
        // The new ArchiveMessagesCompensationAsync path fires after AppendMessagesAsync.
        // With EnableColdArchive=true but no real SQL Server, the fire-and-forget
        // message archive will fail silently. The hot store path must still succeed.
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: true);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await hotStore.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" },
            new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "world" }
        };

        // Should NOT throw — ArchiveMessagesCompensationAsync failure is fire-and-forget
        var result = await dualStore.AppendMessagesAsync("tenant-1", "conv-1", 1, messages);

        Assert.True(result.Success);
        Assert.Equal(2, result.NewVersion);

        // Messages were successfully appended to hot store despite archive failure
        var fetched = await hotStore.GetRecordAsync("tenant-1", "conv-1");
        Assert.Equal(2, fetched!.MessageCount);
        Assert.Equal("hello", fetched.Messages[0].Content);
        Assert.Equal("world", fetched.Messages[1].Content);
    }

    [Fact]
    public async Task AppendMessagesAsync_cold_archive_disabled_succeeds_as_before()
    {
        // Verify that the new ArchiveMessagesCompensationAsync code path
        // does NOT execute when EnableColdArchive is false (no regression).
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        await hotStore.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" }
        };

        var result = await dualStore.AppendMessagesAsync("tenant-1", "conv-1", 1, messages);

        Assert.True(result.Success);
        Assert.Equal(2, result.NewVersion);
        var fetched = await hotStore.GetRecordAsync("tenant-1", "conv-1");
        Assert.Single(fetched!.Messages);
    }

    #endregion

    #region DualWrite with real SQLite cold archive

    [Fact]
    public async Task DualWrite_with_sqlite_cold_archive_create_and_read()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dualwrite_test_{Guid.NewGuid():N}.db");
        try
        {
            var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
            var options = Options.Create(new ConversationStoreOptions
            {
                EnableColdArchive = true,
                ColdArchiveConnectionString = $"Data Source={dbPath}",
                ColdArchiveProvider = "Sqlite"
            });
            var coldArchive = new SqliteConversationRepository(
                options,
                NullLogger<SqliteConversationRepository>.Instance,
                new ConversationStoreMetrics());
            var dualStore = new DualWriteConversationStore(
                hotStore, coldArchive, options,
                NullLogger<DualWriteConversationStore>.Instance,
                CreateWarmer(coldArchive, options),
                new CompensationArchiveService(coldArchive, NullLogger<CompensationArchiveService>.Instance));

            // Create a conversation
            var record = CreateRecord("conv-dw1", "tenant-1", "user-1");
            var result = await dualStore.CreateAsync(record);
            Assert.True(result);

            // Allow fire-and-forget archive to complete
            await Task.Delay(200);

            // Verify hot store has the record
            var hotRecord = await hotStore.GetRecordAsync("tenant-1", "conv-dw1");
            Assert.NotNull(hotRecord);

            // Verify cold archive has the record
            var coldRecord = await coldArchive.GetRecordAsync("tenant-1", "conv-dw1");
            Assert.NotNull(coldRecord);
            Assert.Equal("conv-dw1", coldRecord!.ConversationId);

            coldArchive.Dispose();
        }
        finally
        {
            DeleteFileWithRetry(dbPath);
        }
    }

    [Fact]
    public async Task DualWrite_with_sqlite_cold_archive_append_messages()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dualwrite_test_{Guid.NewGuid():N}.db");
        try
        {
            var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
            var options = Options.Create(new ConversationStoreOptions
            {
                EnableColdArchive = true,
                ColdArchiveConnectionString = $"Data Source={dbPath}",
                ColdArchiveProvider = "Sqlite"
            });
            var coldArchive = new SqliteConversationRepository(
                options,
                NullLogger<SqliteConversationRepository>.Instance,
                new ConversationStoreMetrics());
            var dualStore = new DualWriteConversationStore(
                hotStore, coldArchive, options,
                NullLogger<DualWriteConversationStore>.Instance,
                CreateWarmer(coldArchive, options),
                new CompensationArchiveService(coldArchive, NullLogger<CompensationArchiveService>.Instance));

            // Create conversation
            var record = CreateRecord("conv-dw2", "tenant-1", "user-1");
            await dualStore.CreateAsync(record);
            await Task.Delay(200);

            // Append messages
            var messages = new List<ConversationMessage>
            {
                new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" },
                new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "world" }
            };
            var appendResult = await dualStore.AppendMessagesAsync("tenant-1", "conv-dw2", 1, messages);
            Assert.True(appendResult.Success);

            await Task.Delay(200);

            // Verify cold archive has the messages
            var coldMessages = await coldArchive.LoadMessagesAsync("tenant-1", "conv-dw2");
            Assert.Equal(2, coldMessages.Count);
            Assert.Equal("hello", coldMessages[0].Content);
            Assert.Equal("world", coldMessages[1].Content);

            coldArchive.Dispose();
        }
        finally
        {
            DeleteFileWithRetry(dbPath);
        }
    }

    [Fact]
    public async Task DualWrite_with_sqlite_cold_archive_update_status()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dualwrite_test_{Guid.NewGuid():N}.db");
        try
        {
            var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
            var options = Options.Create(new ConversationStoreOptions
            {
                EnableColdArchive = true,
                ColdArchiveConnectionString = $"Data Source={dbPath}",
                ColdArchiveProvider = "Sqlite"
            });
            var coldArchive = new SqliteConversationRepository(
                options,
                NullLogger<SqliteConversationRepository>.Instance,
                new ConversationStoreMetrics());
            var dualStore = new DualWriteConversationStore(
                hotStore, coldArchive, options,
                NullLogger<DualWriteConversationStore>.Instance,
                CreateWarmer(coldArchive, options),
                new CompensationArchiveService(coldArchive, NullLogger<CompensationArchiveService>.Instance));

            // Create and update
            var record = CreateRecord("conv-dw3", "tenant-1", "user-1");
            await dualStore.CreateAsync(record);
            await Task.Delay(200);

            await dualStore.UpdateStatusAsync("tenant-1", "conv-dw3", ConversationStatus.Completed, 1);
            await Task.Delay(200);

            // Verify cold archive reflects the update
            var coldRecord = await coldArchive.GetRecordAsync("tenant-1", "conv-dw3");
            Assert.NotNull(coldRecord);
            Assert.Equal(ConversationStatus.Completed, coldRecord!.Status);

            coldArchive.Dispose();
        }
        finally
        {
            DeleteFileWithRetry(dbPath);
        }
    }

    #endregion

    [Fact]
    public async Task ListConversationsAsync_delegates_to_hot_store()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        await dualStore.CreateAsync(CreateRecord("conv-1", "tenant-1", "user-1"));
        await dualStore.CreateAsync(CreateRecord("conv-2", "tenant-1", "user-1"));

        var results = await dualStore.ListConversationsAsync("tenant-1", 0, 10);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchConversationsAsync_delegates_to_hot_store()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        record.Messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello world" }
        };
        await hotStore.CreateAsync(record);

        var results = await dualStore.SearchConversationsAsync("tenant-1", "hello", 0, 10);

        Assert.Single(results);
        Assert.Equal("conv-1", results[0].ConversationId);
    }

    [Fact]
    public async Task GetMessagesPagedAsync_delegates_to_hot_store()
    {
        var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var dualStore = CreateDualWriteStore(hotStore, enableColdArchive: false);

        var record = CreateRecord("conv-1", "tenant-1", "user-1");
        record.Messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" },
            new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "world" }
        };
        await hotStore.CreateAsync(record);

        var messages = await dualStore.GetMessagesPagedAsync("tenant-1", "conv-1", 0, 10);

        Assert.Equal(2, messages.Count);
        Assert.Equal("hello", messages[0].Content);
        Assert.Equal("world", messages[1].Content);
    }

    private static void DeleteFileWithRetry(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                break;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static ConversationRecord CreateRecord(string conversationId, string tenantId, string userId) => new()
    {
        ConversationId = conversationId,
        TenantId = tenantId,
        UserId = userId
    };

    private static DualWriteConversationStore CreateDualWriteStore(
        IConversationStore hotStore, bool enableColdArchive)
    {
        var options = Options.Create(new ConversationStoreOptions
        {
            EnableColdArchive = enableColdArchive,
            ColdArchiveConnectionString = "Server=fake;Database=fake;User Id=test;Password=test"
        });

        var archive = new SqlServerConversationRepository(
            options,
            NullLogger<SqlServerConversationRepository>.Instance,
            new ConversationStoreMetrics());

        return new DualWriteConversationStore(
            hotStore,
            archive,
            options,
            NullLogger<DualWriteConversationStore>.Instance,
            CreateWarmer(archive, options),
            new CompensationArchiveService(archive, NullLogger<CompensationArchiveService>.Instance));
    }

    // The warmer's Redis hot store is never exercised in these tests (the InMemory hot store
    // is always pre-populated), so a disconnected RedisConversationStore is sufficient.
    private static ConversationWarmer CreateWarmer(
        IConversationRepository coldArchive, IOptions<ConversationStoreOptions> options) =>
        new(
            new RedisConversationStore(
                connection: null!,
                options,
                NullLogger<RedisConversationStore>.Instance,
                new ConversationStoreMetrics(),
                new RedisTenantIndexManager(options)),
            coldArchive,
            NullLogger<ConversationWarmer>.Instance);
}
