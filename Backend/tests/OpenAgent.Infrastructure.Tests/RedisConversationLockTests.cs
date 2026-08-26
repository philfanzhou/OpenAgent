using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

[Trait("Category", "Container")]
public sealed class RedisConversationLockTests : IAsyncLifetime
{
    private RedisContainer? _redis;
    private IConnectionMultiplexer? _connection;

    public async Task InitializeAsync()
    {
        if (!ContainerTestGuard.Enabled)
        {
            return;
        }

        _redis = new RedisBuilder("redis:7-alpine").Build();
        await _redis.StartAsync().ConfigureAwait(false);
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString()).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        if (_redis != null)
        {
            await _redis.DisposeAsync().ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task SameConversation_IsSerializedAcrossLockInstances()
    {
        ContainerTestGuard.RequireEnabled();
        IConnectionMultiplexer connection = Assert.IsAssignableFrom<IConnectionMultiplexer>(_connection);
        var firstNode = new RedisConversationLock(connection, NullLogger<RedisConversationLock>.Instance);
        var secondNode = new RedisConversationLock(connection, NullLogger<RedisConversationLock>.Instance);

        IConversationLockHandle? first = await firstNode.TryAcquireAsync(
            "tenant-1", "conversation-1", TimeSpan.FromSeconds(10));
        IConversationLockHandle? concurrent = await secondNode.TryAcquireAsync(
            "tenant-1", "conversation-1", TimeSpan.FromSeconds(10));

        Assert.NotNull(first);
        Assert.Null(concurrent);
        await first.DisposeAsync();

        await using IConversationLockHandle? afterRelease = await secondNode.TryAcquireAsync(
            "tenant-1", "conversation-1", TimeSpan.FromSeconds(10));
        Assert.NotNull(afterRelease);
    }

    [SkippableFact]
    public async Task ConversationCache_RoundTripsHotRecord()
    {
        ContainerTestGuard.RequireEnabled();
        IConnectionMultiplexer connection = Assert.IsAssignableFrom<IConnectionMultiplexer>(_connection);
        var cache = new RedisConversationCache(
            connection,
            Options.Create(new ConversationCacheOptions { TimeToLiveMinutes = 1 }));
        ConversationRecord record = new()
        {
            ConversationId = "conversation-cache-1",
            TenantId = "tenant-cache-1",
            UserId = "user-1",
            AgentId = "agent-1",
            Version = 2,
            Messages =
            [
                new ConversationMessage
                {
                    MessageId = "message-cache-1",
                    Sequence = 1,
                    Role = "user",
                    Content = "hot read"
                }
            ]
        };

        await cache.SetAsync(record);
        ConversationRecord? restored = await cache.GetAsync(record.TenantId, record.ConversationId);

        ConversationRecord value = Assert.IsType<ConversationRecord>(restored);
        Assert.Equal(record.Version, value.Version);
        Assert.Equal("hot read", Assert.Single(value.Messages).Content);
    }
}
