using System.Text;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class RouterQueryCacheTests
{
    [Fact]
    public async Task GetAsync_ExpiredMemoryEntry_ReturnsNull()
    {
        var cache = new RouterQueryCache();
        var response = new CachedResponse(
            200,
            "application/json",
            Encoding.UTF8.GetBytes("{}"));

        await cache.SetAsync("key", response, TimeSpan.FromMilliseconds(1));
        await Task.Delay(25);
        CachedResponse? cached = await cache.GetAsync("key");

        Assert.Null(cached);
    }

    [Fact]
    public async Task GetAsync_RedisFailure_PropagatesForMiddlewareFailOpenPolicy()
    {
        var database = new Mock<IDatabase>();
        database.Setup(value => value.StringGetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(
                ConnectionFailureType.UnableToConnect,
                "Redis is unavailable."));
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database.Object);
        var cache = new RouterQueryCache(redis.Object);

        await Assert.ThrowsAsync<RedisConnectionException>(() => cache.GetAsync("key"));
    }
}
