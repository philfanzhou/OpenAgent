using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

internal sealed class AgentConfigManagementService(
    IRedisConnectionProvider redis,
    MockAgentResolver mockAgentResolver,
    AgentConfigLocalStore localStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<AgentConfigEntity?> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable)
        {
            return mockAgentResolver.IsEnabled ? localStore.Get(agentId) : null;
        }

        try
        {
            RedisValue value = await redis.StringGetAsync($"agent:config:{agentId}").ConfigureAwait(false);
            return value.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<AgentConfigEntity>(value.ToString(), JsonOptions);
        }
        catch (RedisException) when (mockAgentResolver.IsEnabled)
        {
            return localStore.Get(agentId);
        }
    }

    internal async Task<AgentConfigEntity?> SaveAsync(
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable)
        {
            if (!mockAgentResolver.IsEnabled)
            {
                throw new InvalidOperationException("Agent configuration store is unavailable.");
            }

            return localStore.Save(agentId, entity, expectedVersion);
        }

        try
        {
            return await SaveToRedisAsync(agentId, entity, expectedVersion).ConfigureAwait(false);
        }
        catch (RedisException) when (mockAgentResolver.IsEnabled)
        {
            return localStore.Save(agentId, entity, expectedVersion);
        }
    }

    private async Task<AgentConfigEntity?> SaveToRedisAsync(
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion)
    {
        string key = $"agent:config:{agentId}";
        IDatabase database = redis.GetDatabase();
        RedisValue currentValue = await database.StringGetAsync(key).ConfigureAwait(false);
        AgentConfigEntity? current = currentValue.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<AgentConfigEntity>(currentValue.ToString(), JsonOptions);

        if (!string.IsNullOrWhiteSpace(expectedVersion)
            && !string.Equals(current?.CurrentVersion, expectedVersion, StringComparison.Ordinal))
        {
            return null;
        }

        entity.AgentId = agentId;
        entity.CurrentVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string payload = JsonSerializer.Serialize(entity, JsonOptions);
        ITransaction transaction = database.CreateTransaction();
        if (currentValue.IsNullOrEmpty)
        {
            transaction.AddCondition(Condition.KeyNotExists(key));
        }
        else
        {
            transaction.AddCondition(Condition.StringEqual(key, currentValue));
        }

        Task<bool> setTask = transaction.StringSetAsync(key, payload);
        bool executed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!executed || !await setTask.ConfigureAwait(false))
        {
            return null;
        }

        await database.SetAddAsync("agent:published:index", agentId).ConfigureAwait(false);
        await database.PublishAsync(
            RedisChannel.Literal("agent:config:updates"),
            JsonSerializer.Serialize(new
            {
                AgentId = agentId,
                Type = "FullConfig",
                Version = entity.CurrentVersion,
                Timestamp = DateTime.UtcNow
            })).ConfigureAwait(false);
        return entity;
    }
}
