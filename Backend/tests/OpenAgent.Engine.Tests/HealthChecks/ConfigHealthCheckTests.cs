using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Redis;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class ConfigHealthCheckTests
{
    [Fact]
    public async Task Returns_healthy_when_no_published_agents()
    {
        var redis = new FakeRedisConnectionProvider();
        var check = new ConfigHealthCheck(redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("Published agents: 0", result.Description);
    }

    [Fact]
    public async Task Returns_healthy_when_published_agents_are_not_cached()
    {
        var redis = new FakeRedisConnectionProvider();
        redis.AddSetMember("agent:published:index", "agent-ok");

        var check = new ConfigHealthCheck(redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("optional", result.Description);
    }

    [Fact]
    public async Task Returns_healthy_when_snapshot_is_empty()
    {
        var redis = new FakeRedisConnectionProvider();
        redis.AddSetMember("agent:published:index", "agent-missing");

        var check = new ConfigHealthCheck(redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Returns_healthy_when_snapshot_is_partially_populated()
    {
        var redis = new FakeRedisConnectionProvider();
        redis.AddSetMember("agent:published:index", "agent-ok");
        redis.AddSetMember("agent:published:index", "agent-missing");

        var check = new ConfigHealthCheck(redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Returns_degraded_when_redis_not_available()
    {
        var redis = new FakeRedisConnectionProvider(available: false);
        var check = new ConfigHealthCheck(redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("cache is optional", result.Description);
    }

    private sealed class FakeRedisConnectionProvider : IRedisConnectionProvider
    {
        private readonly Dictionary<string, HashSet<RedisValue>> _sets = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _isAvailable;

        public FakeRedisConnectionProvider(bool available = true)
        {
            _isAvailable = available;
        }

        public bool IsAvailable => _isAvailable;
        public IServer? GetServer(int database = 0) => null;
        public IDatabase GetDatabase(int database = 0) => throw new NotSupportedException();

        public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(RedisValue.Null);

        public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(true);

        public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
            => Task.FromResult(false);

        public Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            if (_sets.TryGetValue(key!, out var members))
            {
                return Task.FromResult(members.ToArray());
            }
            return Task.FromResult(Array.Empty<RedisValue>());
        }

        public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            if (!_sets.ContainsKey(key!))
            {
                _sets[key!] = new HashSet<RedisValue>();
            }
            _sets[key!].Add(value);
            return Task.FromResult(true);
        }

        public Task<bool> SetRemoveAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return Task.FromResult(_sets.TryGetValue(key!, out var members) && members.Remove(value));
        }

        public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None) => Task.FromResult(TimeSpan.Zero);

        public RedisValue StringGet(RedisKey key, CommandFlags flags = CommandFlags.None) => RedisValue.Null;

        public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler)
        {
        }

        public void Dispose()
        {
        }

        public void AddSetMember(string key, string value)
        {
            if (!_sets.TryGetValue(key, out var set))
            {
                set = new HashSet<RedisValue>();
                _sets[key] = set;
            }
            set.Add(value);
        }
    }
}
