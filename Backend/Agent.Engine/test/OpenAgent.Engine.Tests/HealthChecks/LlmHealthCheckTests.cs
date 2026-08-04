using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Redis;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Engine;
using OpenAgent.Contracts.Models;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class LlmHealthCheckTests
{
    [Fact]
    public async Task Returns_degraded_when_no_published_agents()
    {
        var redis = new FakeRedisConnectionProvider();
        var configProvider = new FakeAgentConfigProvider();
        var check = new LlmHealthCheck(configProvider, redis, NullLogger<LlmHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Returns_unhealthy_when_llm_config_missing()
    {
        var redis = new FakeRedisConnectionProvider();
        redis.AddSetMember("agent:published:index", "agent-no-llm");

        var configWithNullLlm = new AgentConfig { Llm = null! };
        var configProvider = new FakeAgentConfigProvider(configWithNullLlm);
        var check = new LlmHealthCheck(configProvider, redis, NullLogger<LlmHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Returns_healthy_when_llm_config_available()
    {
        var redis = new FakeRedisConnectionProvider();
        redis.AddSetMember("agent:published:index", "agent-llm-ok");

        var configWithLlm = new AgentConfig
        {
            Llm = new LlmConfig { Provider = "openai", Format = ApiFormat.OpenAIChatCompletions, ModelId = "gpt-4o" }
        };
        var configProvider = new FakeAgentConfigProvider(configWithLlm);
        var check = new LlmHealthCheck(configProvider, redis, NullLogger<LlmHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("OpenAIChatCompletions", result.Description);
        Assert.Contains("gpt-4o", result.Description);
    }

    private sealed class FakeRedisConnectionProvider : IRedisConnectionProvider
    {
        private readonly Dictionary<string, HashSet<RedisValue>> _sets = new(StringComparer.OrdinalIgnoreCase);

        public bool IsAvailable => true;
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

    private sealed class FakeAgentConfigProvider : IAgentConfigProvider
    {
        private readonly AgentConfig? _config;

        public FakeAgentConfigProvider(AgentConfig? config = null)
        {
            _config = config;
        }

        public Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromException<AgentConfig>(new InvalidOperationException("Not supported without agentId"));

        public Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default)
            => Task.FromResult(_config);

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentSummary>>(Array.Empty<AgentSummary>());
    }
}
