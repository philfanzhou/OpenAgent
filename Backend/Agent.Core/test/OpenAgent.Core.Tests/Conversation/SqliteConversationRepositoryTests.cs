using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Repository;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class SqliteConversationRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConversationRepository _repository;
    private readonly string _dbPath;

    public SqliteConversationRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sqlite_test_{Guid.NewGuid():N}.db");
        var options = Options.Create(new ConversationStoreOptions
        {
            EnableColdArchive = true,
            ColdArchiveConnectionString = $"Data Source={_dbPath}",
            ColdArchiveProvider = "Sqlite"
        });
        _repository = new SqliteConversationRepository(
            options,
            NullLogger<SqliteConversationRepository>.Instance,
            new ConversationStoreMetrics());
    }

    public async Task InitializeAsync()
    {
        await _repository.EnsureInitializedAsync();
    }

    public Task DisposeAsync()
    {
        _repository.Dispose();
        // Dispose clears the connection pool; small delay for pool cleanup
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
                break;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(100);
            }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void Constructor_throws_when_connection_string_missing()
    {
        var options = Options.Create(new ConversationStoreOptions
        {
            ColdArchiveConnectionString = null
        });

        Assert.Throws<InvalidOperationException>(() =>
            new SqliteConversationRepository(
                options,
                NullLogger<SqliteConversationRepository>.Instance,
                new ConversationStoreMetrics()));
    }

    [Fact]
    public async Task EnsureInitializedAsync_creates_tables()
    {
        Assert.True(File.Exists(_dbPath));

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(ConversationRecords);";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await columnsCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        Assert.Contains("Title", columns);
        Assert.Contains("IsDeletedByUser", columns);
        Assert.Contains("DeletedAt", columns);
        Assert.Contains("ArchivedAt", columns);

        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "SELECT name FROM sqlite_master WHERE name IN ('ConversationMessages', 'IX_Records_Tenant_Deleted');";
        var schemaObjects = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await schemaCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                schemaObjects.Add(reader.GetString(0));
            }
        }

        Assert.Contains("ConversationMessages", schemaObjects);
        Assert.Contains("IX_Records_Tenant_Deleted", schemaObjects);
    }

    [Fact]
    public async Task ArchiveAsync_creates_record()
    {
        var record = CreateRecord("conv-1", "tenant-1", "user-1");

        await _repository.ArchiveAsync(record);

        var fetched = await _repository.GetRecordAsync("tenant-1", "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal("conv-1", fetched!.ConversationId);
        Assert.Equal("tenant-1", fetched.TenantId);
        Assert.Equal("user-1", fetched.UserId);
        Assert.Equal(1, fetched.Version);
        Assert.Equal(ConversationStatus.Running, fetched.Status);
    }

    [Fact]
    public async Task ArchiveAsync_upsert_updates_existing_record()
    {
        var record = CreateRecord("conv-2", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);

        // Update the record
        record.Version = 2;
        record.Status = ConversationStatus.Completed;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.ArchiveAsync(record);

        var fetched = await _repository.GetRecordAsync("tenant-1", "conv-2");
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.Version);
        Assert.Equal(ConversationStatus.Completed, fetched.Status);
    }

    [Fact]
    public async Task ArchiveMessagesAsync_inserts_messages()
    {
        var record = CreateRecord("conv-3", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" },
            new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "world" }
        };

        await _repository.ArchiveMessagesAsync("tenant-1", "conv-3", messages);

        var loaded = await _repository.LoadMessagesAsync("tenant-1", "conv-3");
        Assert.Equal(2, loaded.Count);
        Assert.Equal("hello", loaded[0].Content);
        Assert.Equal("world", loaded[1].Content);
    }

    [Fact]
    public async Task ArchiveMessagesAsync_is_idempotent()
    {
        var record = CreateRecord("conv-4", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" }
        };

        // Write same messages twice
        await _repository.ArchiveMessagesAsync("tenant-1", "conv-4", messages);
        await _repository.ArchiveMessagesAsync("tenant-1", "conv-4", messages);

        var loaded = await _repository.LoadMessagesAsync("tenant-1", "conv-4");
        Assert.Single(loaded); // INSERT OR IGNORE should not duplicate
    }

    [Fact]
    public async Task ArchiveMessagesAsync_with_tool_call_fields()
    {
        var record = CreateRecord("conv-5", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);

        var messages = new List<ConversationMessage>
        {
            new()
            {
                MessageId = "m1", Sequence = 1, Role = "assistant", Content = "calling tool",
                ToolCallId = "tc-1", ToolName = "search"
            }
        };

        await _repository.ArchiveMessagesAsync("tenant-1", "conv-5", messages);

        var loaded = await _repository.LoadMessagesAsync("tenant-1", "conv-5");
        Assert.Single(loaded);
        Assert.Equal("tc-1", loaded[0].ToolCallId);
        Assert.Equal("search", loaded[0].ToolName);
    }

    [Fact]
    public async Task ArchiveMessagesAsync_with_metadata()
    {
        var record = CreateRecord("conv-6", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);

        var messages = new List<ConversationMessage>
        {
            new()
            {
                MessageId = "m1", Sequence = 1, Role = "assistant", Content = "result",
                Metadata = new Dictionary<string, string> { ["model"] = "gpt-4", ["tokens"] = "42" }
            }
        };

        await _repository.ArchiveMessagesAsync("tenant-1", "conv-6", messages);

        var loaded = await _repository.LoadMessagesAsync("tenant-1", "conv-6");
        Assert.Single(loaded);
        Assert.NotNull(loaded[0].Metadata);
        Assert.Equal("gpt-4", loaded[0].Metadata!["model"]);
        Assert.Equal("42", loaded[0].Metadata!["tokens"]);
    }

    [Fact]
    public async Task GetRecordAsync_returns_null_for_nonexistent()
    {
        var fetched = await _repository.GetRecordAsync("tenant-1", "nonexistent");
        Assert.Null(fetched);
    }

    [Fact]
    public async Task LoadMessagesAsync_returns_empty_for_nonexistent()
    {
        var messages = await _repository.LoadMessagesAsync("tenant-1", "nonexistent");
        Assert.Empty(messages);
    }

    [Fact]
    public async Task Tenant_isolation_different_tenants_cannot_access_same_conversation()
    {
        var record = CreateRecord("conv-7", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);

        var fetched = await _repository.GetRecordAsync("tenant-2", "conv-7");
        Assert.Null(fetched);
    }

    [Fact]
    public async Task Full_workflow_create_append_update()
    {
        // 1. Create
        var record = CreateRecord("conv-full", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);

        // 2. Append messages
        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hi" },
            new() { MessageId = "m2", Sequence = 2, Role = "assistant", Content = "hello!" }
        };
        await _repository.ArchiveMessagesAsync("tenant-1", "conv-full", messages);

        // 3. Update status
        record.Version = 2;
        record.Status = ConversationStatus.Completed;
        record.MessageCount = 2;
        await _repository.ArchiveAsync(record);

        // 4. Verify
        var fetched = await _repository.GetRecordAsync("tenant-1", "conv-full");
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.Version);
        Assert.Equal(ConversationStatus.Completed, fetched.Status);
        Assert.Equal(2, fetched.MessageCount);
        Assert.Equal(2, fetched.Messages.Count);
    }

    [Fact]
    public async Task ListConversationsAsync_returns_records_for_tenant()
    {
        for (int i = 1; i <= 3; i++)
        {
            var record = CreateRecord($"conv-list{i}", "tenant-1", "user-1");
            record.LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(i);
            await _repository.ArchiveAsync(record);
        }
        var otherRecord = CreateRecord("conv-other", "tenant-2", "user-2");
        await _repository.ArchiveAsync(otherRecord);

        var results = await _repository.ListConversationsAsync("tenant-1", 0, 10);

        Assert.Equal(3, results.Count);
        // Ordered by LastMessageAt desc
        Assert.Equal("conv-list3", results[0].ConversationId);
        Assert.Equal("conv-list2", results[1].ConversationId);
        Assert.Equal("conv-list1", results[2].ConversationId);
    }

    [Fact]
    public async Task ListConversationsAsync_respects_paging()
    {
        for (int i = 1; i <= 5; i++)
        {
            var record = CreateRecord($"conv-page{i}", "tenant-1", "user-1");
            record.LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(i);
            await _repository.ArchiveAsync(record);
        }

        var results = await _repository.ListConversationsAsync("tenant-1", 2, 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task ListConversationsAsync_returns_empty_for_unknown_tenant()
    {
        var results = await _repository.ListConversationsAsync("nonexistent-tenant", 0, 10);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchConversationsAsync_finds_matching_content()
    {
        var record = CreateRecord("conv-search1", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);
        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello world" }
        };
        await _repository.ArchiveMessagesAsync("tenant-1", "conv-search1", messages);

        var results = await _repository.SearchConversationsAsync("tenant-1", "hello", 0, 10);

        Assert.Single(results);
        Assert.Equal("conv-search1", results[0].ConversationId);
    }

    [Fact]
    public async Task SearchConversationsAsync_no_match()
    {
        var record = CreateRecord("conv-search2", "tenant-1", "user-1");
        await _repository.ArchiveAsync(record);
        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello world" }
        };
        await _repository.ArchiveMessagesAsync("tenant-1", "conv-search2", messages);

        var results = await _repository.SearchConversationsAsync("tenant-1", "nonexistent", 0, 10);

        Assert.Empty(results);
    }

    private static ConversationRecord CreateRecord(string conversationId, string tenantId, string userId) => new()
    {
        ConversationId = conversationId,
        TenantId = tenantId,
        UserId = userId
    };
}
