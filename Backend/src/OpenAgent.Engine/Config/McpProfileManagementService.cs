using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

internal sealed class McpProfileManagementService(
    IRedisConnectionProvider redis,
    IMcpRegistry registry)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal async Task<IReadOnlyList<McpServerConfig>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable) return registry.GetAll();

        try
        {
            RedisValue[] ids = await redis.SetMembersAsync("mcp:published:index").ConfigureAwait(false);
            var servers = new List<McpServerConfig>();
            foreach (RedisValue id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                McpServerConfig? server = await GetFromRedisAsync(id.ToString()).ConfigureAwait(false);
                if (server != null) servers.Add(server);
            }
            return servers.Count > 0 ? servers : registry.GetAll();
        }
        catch (RedisException)
        {
            return registry.GetAll();
        }
    }

    internal async Task<IReadOnlyList<McpServerConfig>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<McpServerConfig> servers = await ListAsync(cancellationToken).ConfigureAwait(false);
        return servers
            .Where(server => string.Equals(server.TenantId, tenantId, StringComparison.Ordinal))
            .ToArray();
    }

    internal async Task<McpServerConfig?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable) return registry.Get(id);
        try
        {
            return await GetFromRedisAsync(id).ConfigureAwait(false) ?? registry.Get(id);
        }
        catch (RedisException)
        {
            return registry.Get(id);
        }
    }

    internal async Task<McpServerConfig?> GetAsync(
        string id,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        McpServerConfig? server = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        return server != null
            && string.Equals(server.TenantId, tenantId, StringComparison.Ordinal)
            ? server
            : null;
    }

    internal async Task<McpServerConfig> SaveAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable)
        {
            McpServerConfig? current = registry.Get(server.Name);
            if (current != null
                && !string.IsNullOrWhiteSpace(current.TenantId)
                && !string.Equals(current.TenantId, server.TenantId, StringComparison.Ordinal))
            {
                throw new TenantDataIsolationException(
                    server.TenantId,
                    current.TenantId,
                    "MCP profile does not belong to the authenticated tenant.");
            }

            registry.Register(server);
            return server;
        }

        string key = $"mcp:registry:{server.Name}";
        IDatabase database = redis.GetDatabase();
        RedisValue currentValue = await database.StringGetAsync(key).ConfigureAwait(false);
        McpServerConfig? existing = currentValue.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<McpServerConfig>(currentValue.ToString(), JsonOptions);
        if (existing != null
            && !string.IsNullOrWhiteSpace(existing.TenantId)
            && !string.Equals(existing.TenantId, server.TenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                server.TenantId,
                existing.TenantId,
                "MCP profile does not belong to the authenticated tenant.");
        }

        string payload = JsonSerializer.Serialize(server, JsonOptions);
        ITransaction transaction = database.CreateTransaction();
        transaction.AddCondition(currentValue.IsNullOrEmpty
            ? Condition.KeyNotExists(key)
            : Condition.StringEqual(key, currentValue));
        Task<bool> setTask = transaction.StringSetAsync(key, payload);
        Task<long> publishTask = transaction.PublishAsync(
            RedisChannel.Literal("mcp:registry:changed"),
            server.Name);
        bool executed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!executed || !await setTask.ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to save MCP profile '{server.Name}' to Redis.");
        }

        await publishTask.ConfigureAwait(false);
        registry.Register(server);
        await redis.SetAddAsync("mcp:published:index", server.Name).ConfigureAwait(false);
        return server;
    }

    internal async Task<McpServerConfig> SaveAsync(
        McpServerConfig server,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        McpServerConfig? existing = await GetAsync(server.Name, cancellationToken).ConfigureAwait(false);
        if (existing != null
            && !string.Equals(existing.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                tenantId,
                existing.TenantId,
                "MCP profile does not belong to the authenticated tenant.");
        }

        server.TenantId = tenantId;
        return await SaveAsync(server, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool removed = registry.Remove(id);
        if (!redis.IsAvailable) return removed;

        bool deleted = await redis.KeyDeleteAsync($"mcp:registry:{id}").ConfigureAwait(false);
        await redis.SetRemoveAsync("mcp:published:index", id).ConfigureAwait(false);
        await redis.GetDatabase().PublishAsync(RedisChannel.Literal("mcp:registry:changed"), id).ConfigureAwait(false);
        return deleted || removed;
    }

    internal async Task<bool> DeleteAsync(
        string id,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        McpServerConfig? existing = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing == null
            || !string.Equals(existing.TenantId, tenantId, StringComparison.Ordinal))
        {
            return false;
        }

        return await DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpServerConfig?> GetFromRedisAsync(string id)
    {
        RedisValue value = await redis.StringGetAsync($"mcp:registry:{id}").ConfigureAwait(false);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<McpServerConfig>(value.ToString(), JsonOptions);
    }
}
