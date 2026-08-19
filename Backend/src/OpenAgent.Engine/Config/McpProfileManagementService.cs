using System.Text.Json;
using OpenAgent.Contracts.Configuration;
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

    internal async Task<McpServerConfig> SaveAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        registry.Register(server);
        if (!redis.IsAvailable) return server;

        string payload = JsonSerializer.Serialize(server, JsonOptions);
        await redis.StringSetAsync($"mcp:registry:{server.Name}", payload).ConfigureAwait(false);
        await redis.SetAddAsync("mcp:published:index", server.Name).ConfigureAwait(false);
        await redis.GetDatabase().PublishAsync(RedisChannel.Literal("mcp:registry:changed"), server.Name).ConfigureAwait(false);
        return server;
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

    private async Task<McpServerConfig?> GetFromRedisAsync(string id)
    {
        RedisValue value = await redis.StringGetAsync($"mcp:registry:{id}").ConfigureAwait(false);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<McpServerConfig>(value.ToString(), JsonOptions);
    }
}
