using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

/// <summary>
/// Manages tenant-owned Agent and LLM configurations and their disposable Redis caches.
/// </summary>
public sealed class ConfigurationService : IAgentConfigProvider, ILlmConfigProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAgentConfigRepository _agents;
    private readonly ILlmConfigRepository _models;
    private readonly IRedisConnectionProvider _redis;
    private readonly ILogger<ConfigurationService> _logger;
    private readonly TimeSpan _cacheTtl;

    internal ConfigurationService(
        IAgentConfigRepository agents,
        ILlmConfigRepository models,
        IRedisConnectionProvider redis,
        IOptions<AgentConfigSourceOptions> options,
        ILogger<ConfigurationService> logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Value.RedisCacheTtlSeconds);
        _agents = agents;
        _models = models;
        _redis = redis;
        _logger = logger;
        _cacheTtl = TimeSpan.FromSeconds(options.Value.RedisCacheTtlSeconds);
    }

    public async Task<AgentConfig?> GetConfigAsync(
        string agentId, string tenantId, CancellationToken cancellationToken = default)
    {
        AgentConfigEntity? entity = await ReadCachedAsync(
            BuildCacheKey("agent", tenantId, agentId),
            () => _agents.GetAsync(tenantId, agentId, cancellationToken),
            cached => cached.TenantId == tenantId && cached.AgentId == agentId,
            cancellationToken).ConfigureAwait(false);
        return entity?.Config;
    }

    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AgentConfigEntity> entities = await _agents
            .ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return entities.Select(entity => new AgentSummary
        {
            TenantId = entity.TenantId,
            AgentId = entity.AgentId,
            Name = entity.Name,
            Description = entity.Description,
            Status = (int)entity.Status,
            CurrentVersion = entity.CurrentVersion
        }).ToArray();
    }

    internal Task<AgentConfigEntity?> GetAgentAsync(
        string agentId, string tenantId, CancellationToken cancellationToken = default) =>
        _agents.GetAsync(tenantId, agentId, cancellationToken);

    internal async Task<AgentConfigEntity?> SaveAgentAsync(
        string agentId, string tenantId, AgentConfigEntity entity, string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        entity.AgentId = agentId;
        entity.TenantId = tenantId;
        entity.Config.TenantId = tenantId;
        foreach (McpServerConfig server in entity.Config.Mcp.Servers)
            server.TenantId = tenantId;
        foreach (RagInstanceConfig rag in entity.Config.Rag.Instances)
            rag.AllowedTenantIds = [tenantId];
        foreach (SkillInstanceConfig skill in entity.Config.Skills.Instances)
        {
            skill.TenantId = tenantId;
            skill.AllowedTenantIds = [tenantId];
        }

        AgentConfigEntity? saved = await _agents.UpsertAsync(
            tenantId, agentId, entity,
            string.IsNullOrWhiteSpace(expectedVersion) ? entity.CurrentVersion : expectedVersion,
            cancellationToken).ConfigureAwait(false);
        if (saved != null)
            await WriteCacheAsync(BuildCacheKey("agent", tenantId, agentId), saved).ConfigureAwait(false);
        return saved;
    }

    public Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
        string tenantId, CancellationToken cancellationToken = default) =>
        _models.ListAsync(tenantId, cancellationToken);

    public Task<LlmProviderProfile?> GetAsync(
        string tenantId, string profileId, CancellationToken cancellationToken = default) =>
        ReadCachedAsync(
            BuildCacheKey("llm", tenantId, profileId),
            () => _models.GetAsync(tenantId, profileId, cancellationToken),
            cached => cached.TenantId == tenantId && cached.Id == profileId,
            cancellationToken);

    internal async Task<LlmProviderProfile> SaveLlmAsync(
        LlmProviderProfile profile, string tenantId, CancellationToken cancellationToken = default)
    {
        LlmProviderProfile? existing = await _models
            .GetAsync(tenantId, profile.Id, cancellationToken).ConfigureAwait(false);
        profile.TenantId = tenantId;
        if (existing != null && (string.IsNullOrWhiteSpace(profile.ApiKey)
            || profile.ApiKey.StartsWith("***", StringComparison.Ordinal)))
            profile.ApiKey = existing.ApiKey;

        LlmProviderProfile saved = await _models
            .UpsertAsync(tenantId, profile.Id, profile, cancellationToken).ConfigureAwait(false);
        await WriteCacheAsync(BuildCacheKey("llm", tenantId, profile.Id), saved).ConfigureAwait(false);
        return saved;
    }

    internal async Task<bool> DeleteLlmAsync(
        string profileId, string tenantId, CancellationToken cancellationToken = default)
    {
        bool deleted = await _models.DeleteAsync(tenantId, profileId, cancellationToken).ConfigureAwait(false);
        if (_redis.IsAvailable)
        {
            try
            {
                await _redis.KeyDeleteAsync(BuildCacheKey("llm", tenantId, profileId)).ConfigureAwait(false);
            }
            catch (RedisException exception)
            {
                EngineLog.LlmConfigCacheEvictionFailed(_logger, exception, profileId);
            }
        }
        return deleted;
    }

    // A schema version prevents older cache payloads from dropping renamed fields.
    internal static string BuildCacheKey(string kind, string tenantId, string id) =>
        $"{kind}:config-cache:v2:{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(id)}";

    private async Task<T?> ReadCachedAsync<T>(
        string key, Func<Task<T?>> load, Func<T, bool> matches,
        CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_redis.IsAvailable)
        {
            try
            {
                RedisValue value = await _redis.StringGetAsync(key).ConfigureAwait(false);
                if (!value.IsNullOrEmpty)
                {
                    T? cached = JsonSerializer.Deserialize<T>(value.ToString(), JsonOptions);
                    if (cached != null && matches(cached)) return cached;
                }
            }
            catch (Exception exception) when (exception is RedisException or JsonException or InvalidOperationException)
            {
                EngineLog.ConfigurationCacheFailed(_logger, "read", key, exception.GetType().Name);
            }
        }

        T? persisted = await load().ConfigureAwait(false);
        if (persisted != null) await WriteCacheAsync(key, persisted).ConfigureAwait(false);
        return persisted;
    }

    private async Task WriteCacheAsync<T>(string key, T value)
    {
        if (!_redis.IsAvailable) return;
        try
        {
            await _redis.StringSetAsync(key, JsonSerializer.Serialize(value, JsonOptions), _cacheTtl)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RedisException or InvalidOperationException)
        {
            EngineLog.ConfigurationCacheFailed(_logger, "write", key, exception.GetType().Name);
        }
    }
}
