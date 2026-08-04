using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Redis;
using OpenAgent.Contracts.Configuration;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class ConfigHealthCheckTests
{
    private static ConfigSnapshot CreateSnapshot()
    {
        return new ConfigSnapshot(
            Options.Create(new ConfigSnapshotOptions()),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ConfigSnapshot>.Instance);
    }

    [Fact]
    public async Task Returns_degraded_when_no_published_agents()
    {
        var redis = new FakeRedisConnectionProvider();
        var snapshot = CreateSnapshot();
        var check = new ConfigHealthCheck(snapshot, redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Returns_healthy_when_snapshot_fully_populated()
    {
        var redis = new FakeRedisConnectionProvider();
        redis.AddSetMember("agent:published:index", "agent-ok");

        var snapshot = CreateSnapshot();
        snapshot.SetFullConfig("agent-ok", new AgentConfig
        {
            Llm = new LlmConfig { Provider = "openai", Format = ApiFormat.OpenAIChatCompletions }
        });

        var check = new ConfigHealthCheck(snapshot, redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("fully populated", result.Description);
    }

    [Fact]
    public async Task Returns_unhealthy_when_snapshot_is_empty()
    {
        var redis = new FakeRedisConnectionProvider();
        redis.AddSetMember("agent:published:index", "agent-missing");

        var snapshot = CreateSnapshot();
        var check = new ConfigHealthCheck(snapshot, redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("empty", result.Description);
    }

    [Fact]
    public async Task Returns_degraded_when_snapshot_partially_populated()
    {
        var redis = new FakeRedisConnectionProvider();
        redis.AddSetMember("agent:published:index", "agent-ok");
        redis.AddSetMember("agent:published:index", "agent-missing");

        var snapshot = CreateSnapshot();
        snapshot.SetFullConfig("agent-ok", new AgentConfig
        {
            Llm = new LlmConfig { Provider = "openai", Format = ApiFormat.OpenAIChatCompletions }
        });

        var check = new ConfigHealthCheck(snapshot, redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("partially populated", result.Description);
    }

    [Fact]
    public async Task Returns_degraded_when_redis_not_available()
    {
        var redis = new FakeRedisConnectionProvider(available: false);
        var snapshot = CreateSnapshot();
        var check = new ConfigHealthCheck(snapshot, redis);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Redis is not available", result.Description);
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
