using Moq;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class IdempotencyStoreTests
{
    [Fact]
    public async Task AcquireAsync_ExpiredPendingEntry_CanBeAcquiredAgain()
    {
        var store = new IdempotencyStore();

        IdempotencyAcquireResult first = await store.AcquireAsync(
            "key",
            "digest",
            "owner-1",
            TimeSpan.FromMilliseconds(1));
        await Task.Delay(25);
        IdempotencyAcquireResult second = await store.AcquireAsync(
            "key",
            "digest",
            "owner-2",
            TimeSpan.FromMinutes(1));

        Assert.Equal(IdempotencyAcquireStatus.Acquired, first.Status);
        Assert.Equal(IdempotencyAcquireStatus.Acquired, second.Status);
    }

    [Fact]
    public async Task AcquireAsync_ExpiredCompletedEntry_CanBeAcquiredAgain()
    {
        var store = new IdempotencyStore();
        await store.AcquireAsync(
            "key",
            "digest",
            "owner-1",
            TimeSpan.FromMinutes(1));
        await store.CompleteAsync(
            "key",
            "digest",
            "owner-1",
            new CachedResponse(200, "application/json", []),
            TimeSpan.FromMilliseconds(1));
        await Task.Delay(25);

        IdempotencyAcquireResult result = await store.AcquireAsync(
            "key",
            "digest",
            "owner-2",
            TimeSpan.FromMinutes(1));

        Assert.Equal(IdempotencyAcquireStatus.Acquired, result.Status);
    }

    [Fact]
    public async Task AcquireAsync_RedisBackend_UsesAtomicSetWhenNotExists()
    {
        var database = new Mock<IDatabase>();
        database.Setup(value => value.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                TimeSpan.FromSeconds(30),
                When.NotExists))
            .ReturnsAsync(true);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database.Object);
        var store = new IdempotencyStore(redis.Object);

        IdempotencyAcquireResult result = await store.AcquireAsync(
            "key",
            "digest",
            "owner",
            TimeSpan.FromSeconds(30));

        Assert.Equal(IdempotencyAcquireStatus.Acquired, result.Status);
        database.VerifyAll();
    }
}
