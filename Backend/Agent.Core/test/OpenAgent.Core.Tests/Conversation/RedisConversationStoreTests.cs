using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Store;
using StackExchange.Redis;
using Xunit;
using Xunit.Sdk;

namespace OpenAgent.Core.Tests.Conversation;

/// <summary>
/// Integration tests for RedisConversationStore.
/// Requires a local Redis at localhost:6379; tests are skipped automatically when Redis is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public class RedisConversationStoreTests : IAsyncLifetime
{
    private IConnectionMultiplexer? _redis;
    private RedisConversationStore? _sut;

    public async Task InitializeAsync()
    {
        try
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(
                "localhost:6379,abortConnect=false,connectTimeout=500");
            if (!_redis.IsConnected) throw new InvalidOperationException("Redis not connected");
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Redis unavailable at localhost:6379: {ex.Message}");
        }
        var options = Options.Create(new ConversationStoreOptions { RedisTtlMinutes = 10 });
        _sut = new RedisConversationStore(
            _redis!,
            options,
            NullLogger<RedisConversationStore>.Instance,
            new ConversationStoreMetrics(),
            new RedisTenantIndexManager(options));
    }

    public async Task DisposeAsync()
    {
        if (_redis == null) return;
        try
        {
            await _redis.DisposeAsync();
        }
        catch
        {
            // ignored
        }
    }

    [SkippableFact]
    public async Task AppendMessagesAsync_appends_and_increments_version()
    {
        var tenantId = $"test-{Guid.NewGuid():N}";
        var record = CreateRecord("conv-1", tenantId, "user-1");
        await _sut!.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" }
        };

        var result = await _sut.AppendMessagesAsync(tenantId, "conv-1", 1, messages);

        Assert.True(result.Success);
        Assert.Equal(2, result.NewVersion);
        Assert.Equal(1, result.NewMessageCount);

        var fetched = await _sut.GetRecordAsync(tenantId, "conv-1");
        Assert.NotNull(fetched);
        Assert.Single(fetched!.Messages);
        Assert.Equal(2, fetched.Version);
    }

    [SkippableFact]
    public async Task AppendMessagesAsync_fails_on_version_conflict()
    {
        var tenantId = $"test-{Guid.NewGuid():N}";
        var record = CreateRecord("conv-1", tenantId, "user-1");
        await _sut!.CreateAsync(record);

        var messages = new List<ConversationMessage>
        {
            new() { MessageId = "m1", Sequence = 1, Role = "user", Content = "hello" }
        };

        var result = await _sut.AppendMessagesAsync(tenantId, "conv-1", 999, messages);

        Assert.False(result.Success);
        Assert.Contains("Version conflict", result.ConflictReason);
    }

    [SkippableFact]
    public async Task AppendMessagesAsync_concurrent_appends_are_atomic()
    {
        var tenantId = $"test-{Guid.NewGuid():N}";
        var record = CreateRecord("conv-1", tenantId, "user-1");
        await _sut!.CreateAsync(record);

        const int taskCount = 100;
        var tasks = new List<Task<AppendResult>>();
        for (int i = 0; i < taskCount; i++)
        {
            var version = 1 + i;
            var messages = new List<ConversationMessage>
            {
                new() { MessageId = $"m-{i}", Sequence = i + 1, Role = "user", Content = $"hello-{i}" }
            };
            tasks.Add(_sut.AppendMessagesAsync(tenantId, "conv-1", version, messages));
        }

        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.Success);
        Assert.Equal(taskCount, successCount);

        var fetched = await _sut.GetRecordAsync(tenantId, "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal(taskCount + 1, fetched!.Version);
        Assert.Equal(taskCount, fetched.Messages.Count);
    }

    [SkippableFact]
    public async Task AppendMessagesAsync_concurrent_same_expected_version_one_succeeds()
    {
        var tenantId = $"test-{Guid.NewGuid():N}";
        var record = CreateRecord("conv-1", tenantId, "user-1");
        await _sut!.CreateAsync(record);

        const int taskCount = 10;
        var tasks = new List<Task<AppendResult>>();
        for (int i = 0; i < taskCount; i++)
        {
            var messages = new List<ConversationMessage>
            {
                new() { MessageId = $"m-{i}", Sequence = i + 1, Role = "user", Content = $"hello-{i}" }
            };
            tasks.Add(_sut.AppendMessagesAsync(tenantId, "conv-1", expectedVersion: 1, messages));
        }

        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.Success);
        Assert.Equal(1, successCount);

        var fetched = await _sut.GetRecordAsync(tenantId, "conv-1");
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.Version);
    }

    private static ConversationRecord CreateRecord(string conversationId, string tenantId, string userId) => new()
    {
        ConversationId = conversationId,
        TenantId = tenantId,
        UserId = userId
    };
}
