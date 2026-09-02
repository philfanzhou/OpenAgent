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
/// PostgreSQL-owned Agent configuration with a tenant-scoped Redis cache.
/// </summary>
internal sealed class AgentConfigDatabaseStore
{
    internal const string CacheIndexKeyPrefix = "agent:config-cache:index:";
    internal const string CacheKeyPrefix = "agent:config-cache:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IRedisConnectionProvider _redis;
    private readonly AgentConfigSourceOptions _options;
    private readonly ILogger _logger;
    private readonly IAgentConfigRepository _repository;

    public AgentConfigDatabaseStore(
        IRedisConnectionProvider redis,
        IOptions<AgentConfigSourceOptions> options,
        ILogger<AgentConfigDatabaseStore> logger,
        IAgentConfigRepository repository)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
        _repository = repository;

        if (_options.RedisCacheTtlSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                AgentConfigSourceOptions.SectionName,
                _options.RedisCacheTtlSeconds,
                "ConfigurationStore:RedisCacheTtlSeconds must be greater than zero.");
        }
        if (_options.RedisCacheReconciliationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                AgentConfigSourceOptions.SectionName,
                _options.RedisCacheReconciliationSeconds,
                "ConfigurationStore:RedisCacheReconciliationSeconds must be greater than zero.");
        }
    }

    internal TimeSpan ReconciliationInterval =>
        TimeSpan.FromSeconds(_options.RedisCacheReconciliationSeconds);

    internal Task<AgentConfigEntity?> GetAuthoritativeAsync(
        string tenantId,
        string agentId,
        CancellationToken cancellationToken) =>
        _repository.GetAsync(tenantId, agentId, cancellationToken);

    internal Task<IReadOnlyList<AgentConfigEntity>> ListAuthoritativeAsync(
        string? tenantId,
        CancellationToken cancellationToken) =>
        _repository.ListAsync(tenantId, cancellationToken);

    internal async Task<AgentConfigEntity?> GetRuntimeAsync(
        string tenantId,
        string agentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_redis.IsAvailable)
        {
            try
            {
                RedisValue value = await _redis
                    .StringGetAsync(BuildCacheKey(tenantId, agentId))
                    .ConfigureAwait(false);
                if (!value.IsNullOrEmpty)
                {
                    AgentConfigEntity? cached = Deserialize(value.ToString());
                    if (MatchesAgent(cached, tenantId, agentId))
                    {
                        return cached;
                    }
                }
            }
            catch (Exception exception) when (exception is RedisException or JsonException)
            {
                EngineLog.AgentConfigCacheReadFailed(_logger, exception, agentId);
            }
        }

        AgentConfigEntity? entity = await _repository
            .GetAsync(tenantId, agentId, cancellationToken)
            .ConfigureAwait(false);
        if (entity != null)
        {
            EngineLog.AgentConfigLoadedFromPostgreSql(_logger, agentId, entity.CurrentVersion);
            await TryWriteCacheAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        return entity;
    }

    internal async Task<AgentConfigEntity?> SaveAsync(
        string tenantId,
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        string? effectiveVersion = string.IsNullOrWhiteSpace(expectedVersion)
            ? entity.CurrentVersion
            : expectedVersion;
        AgentConfigEntity? saved = await _repository
            .UpsertAsync(tenantId, agentId, entity, effectiveVersion, cancellationToken)
            .ConfigureAwait(false);
        if (saved != null)
        {
            await TryWriteCacheAsync(saved, CancellationToken.None).ConfigureAwait(false);
        }
        return saved;
    }

    internal async Task<bool> TryWarmupAsync(CancellationToken cancellationToken)
    {
        if (!_redis.IsAvailable)
        {
            return false;
        }

        IReadOnlyList<AgentConfigEntity> agents = await _repository
            .ListAsync(tenantId: null, cancellationToken)
            .ConfigureAwait(false);
        int cached = 0;
        foreach (AgentConfigEntity agent in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryWriteCacheAsync(agent, cancellationToken).ConfigureAwait(false))
            {
                cached++;
            }
        }

        EngineLog.AgentConfigCacheWarmupCompleted(_logger, cached, agents.Count);
        return cached == agents.Count;
    }

    private async Task<bool> TryWriteCacheAsync(
        AgentConfigEntity entity,
        CancellationToken cancellationToken)
    {
        if (!_redis.IsAvailable)
        {
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool stored = await _redis.StringSetAsync(
                BuildCacheKey(entity.TenantId, entity.AgentId),
                JsonSerializer.Serialize(entity, JsonOptions),
                TimeSpan.FromSeconds(_options.RedisCacheTtlSeconds)).ConfigureAwait(false);
            if (stored)
            {
                await _redis.SetAddAsync(
                    BuildCacheIndexKey(entity.TenantId),
                    entity.AgentId).ConfigureAwait(false);
            }
            return stored;
        }
        catch (Exception exception) when (exception is RedisException or InvalidOperationException)
        {
            EngineLog.AgentConfigCacheWriteFailed(_logger, exception, entity.AgentId);
            return false;
        }
    }

    private static AgentConfigEntity? Deserialize(string payload) =>
        JsonSerializer.Deserialize<AgentConfigEntity>(payload, JsonOptions);

    private static bool MatchesAgent(
        AgentConfigEntity? entity,
        string tenantId,
        string agentId) =>
        entity?.Config != null
        && string.Equals(entity.TenantId, tenantId, StringComparison.Ordinal)
        && string.Equals(entity.AgentId, agentId, StringComparison.OrdinalIgnoreCase);

    internal static string BuildCacheKey(string tenantId, string agentId) =>
        $"{CacheKeyPrefix}{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(agentId)}";

    internal static string BuildCacheIndexKey(string tenantId) =>
        $"{CacheIndexKeyPrefix}{Uri.EscapeDataString(tenantId)}";
}
