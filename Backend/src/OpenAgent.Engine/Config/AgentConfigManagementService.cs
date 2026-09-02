using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Reload;
using OpenAgent.Engine.Reload.Dtos;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

internal sealed class AgentConfigManagementService(
    IRedisConnectionProvider redis,
    MockAgentResolver mockAgentResolver,
    AgentConfigLocalStore localStore,
    ConfigUpdateDispatcher configUpdates,
    AgentConfigDatabaseStore? databaseStore = null)
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
        if (databaseStore != null)
        {
            throw new InvalidOperationException(
                "TenantId is required to load PostgreSQL-backed Agent configuration.");
        }

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

    internal async Task<AgentConfigEntity?> GetAsync(
        string agentId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (databaseStore != null)
        {
            return await databaseStore
                .GetAuthoritativeAsync(tenantId, agentId, cancellationToken)
                .ConfigureAwait(false);
        }

        AgentConfigEntity? entity = await GetAsync(agentId, cancellationToken).ConfigureAwait(false);
        return entity != null
            && string.Equals(ResolveTenant(entity), tenantId, StringComparison.Ordinal)
            ? entity
            : null;
    }

    internal async Task<AgentConfigEntity?> SaveAsync(
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (databaseStore != null)
        {
            throw new InvalidOperationException(
                "TenantId is required to save PostgreSQL-backed Agent configuration.");
        }

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

    internal async Task<AgentConfigEntity?> SaveAsync(
        string agentId,
        string tenantId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (databaseStore != null)
        {
            StampTenant(entity, tenantId);
            return await databaseStore
                .SaveAsync(tenantId, agentId, entity, expectedVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        AgentConfigEntity? current = await GetAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (current != null
            && !string.Equals(ResolveTenant(current), tenantId, StringComparison.Ordinal))
        {
            return null;
        }

        StampTenant(entity, tenantId);
        return await SaveAsync(
            agentId,
            entity,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
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

        if (current != null
            && !string.IsNullOrWhiteSpace(ResolveTenant(current))
            && !string.Equals(ResolveTenant(current), ResolveTenant(entity), StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(expectedVersion)
            && !string.Equals(current?.CurrentVersion, expectedVersion, StringComparison.Ordinal))
        {
            return null;
        }

        entity.AgentId = agentId;
        long version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        entity.CurrentVersion = version.ToString();
        string payload = JsonSerializer.Serialize(entity, JsonOptions);
        string notification = JsonSerializer.Serialize(new ConfigUpdate
        {
            ResourceType = ConfigUpdate.AgentResourceType,
            ResourceId = agentId,
            Operation = ConfigUpdate.UpsertOperation,
            Version = version,
            Timestamp = DateTimeOffset.UtcNow
        }, ConfigUpdateDispatcher.JsonOptions);
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
        Task<long> publishTask = transaction.PublishAsync(
            RedisChannel.Literal(HotReloadService.CurrentUpdatesChannel),
            notification);
        bool executed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!executed || !await setTask.ConfigureAwait(false))
        {
            return null;
        }

        await publishTask.ConfigureAwait(false);
        if (!configUpdates.Process(HotReloadService.CurrentUpdatesChannel, notification))
        {
            throw new InvalidOperationException(
                $"Agent configuration '{agentId}' was saved, but the local cache could not be refreshed.");
        }

        await database.SetAddAsync("agent:published:index", agentId).ConfigureAwait(false);
        return entity;
    }

    private static string ResolveTenant(AgentConfigEntity entity) =>
        string.IsNullOrWhiteSpace(entity.TenantId)
            ? entity.Config.TenantId
            : entity.TenantId;

    private static void StampTenant(AgentConfigEntity entity, string tenantId)
    {
        entity.TenantId = tenantId;
        entity.Config.TenantId = tenantId;
        foreach (McpServerConfig server in entity.Config.Mcp.Servers)
        {
            server.TenantId = tenantId;
        }
        foreach (RagInstanceConfig rag in entity.Config.Rag.Instances)
        {
            rag.AllowedTenantIds = [tenantId];
        }
        foreach (SkillInstanceConfig skill in entity.Config.Skills.Instances)
        {
            skill.TenantId = tenantId;
            skill.AllowedTenantIds = [tenantId];
        }
    }
}
