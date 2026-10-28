using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Redis;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class RedisHealthCheckTests
{
    [Fact]
    public async Task Returns_degraded_when_redis_not_available()
    {
        var redis = new FakeRedisConnectionProvider { IsAvailableValue = false };
        var check = new RedisHealthCheck(redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Returns_healthy_when_ping_succeeds()
    {
        var redis = new FakeRedisConnectionProvider { IsAvailableValue = true };
        var check = new RedisHealthCheck(redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Returns_unhealthy_when_ping_throws()
    {
        var redis = new FakeRedisConnectionProvider
        {
            IsAvailableValue = true,
            PingException = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "connection lost")
        };
        var check = new RedisHealthCheck(redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    private sealed class FakeRedisConnectionProvider : IRedisConnectionProvider
    {
        public bool IsAvailableValue { get; set; } = true;
        public bool IsAvailable => IsAvailableValue;
        public Exception? PingException { get; set; }

        public IServer? GetServer(int database = 0) => null;
        public IDatabase GetDatabase(int database = 0) => throw new NotSupportedException();

        public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(RedisValue.Null);

        public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(true);

        public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(false);

        public Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(Array.Empty<RedisValue>());

        public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(true);

        public Task<bool> SetRemoveAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(true);

        public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None)
        {
            if (PingException != null)
            {
                throw PingException;
            }
            return Task.FromResult(TimeSpan.FromMilliseconds(1));
        }

        public RedisValue StringGet(RedisKey key, CommandFlags flags = CommandFlags.None) => RedisValue.Null;
        public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler) { }
        public void Dispose() { }
    }
}
