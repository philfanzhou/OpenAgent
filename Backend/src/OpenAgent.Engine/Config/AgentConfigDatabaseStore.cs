using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;
using OpenAgent.Engine.Reload;
using OpenAgent.Engine.Reload.Dtos;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

/// <summary>
/// Opt-in proof of concept for PostgreSQL-owned Agent configuration with a
/// disposable Redis cache. The legacy Redis source remains untouched.
/// </summary>
internal sealed class AgentConfigDatabaseStore
{
    internal const string CacheIndexKey = "agent:config-cache:index";
    internal const string CacheKeyPrefix = "agent:config-cache:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IRedisConnectionProvider _redis;
    private readonly ConfigSnapshot _snapshot;
    private readonly AgentConfigSourceOptions _options;
    private readonly ILogger _logger;
    private readonly IAgentConfigRepository? _repository;

    public AgentConfigDatabaseStore(
        IRedisConnectionProvider redis,
        ConfigSnapshot snapshot,
        IOptions<AgentConfigSourceOptions> options,
        ILogger<AgentConfigDatabaseStore> logger,
        IAgentConfigRepository? repository = null)
    {
        _redis = redis;
        _snapshot = snapshot;
        _options = options.Value;
        _logger = logger;
        _repository = repository;

        if (_options.UsePostgreSqlForAgents && _options.RedisCacheTtlSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                AgentConfigSourceOptions.SectionName,
                _options.RedisCacheTtlSeconds,
                "ConfigurationStore:RedisCacheTtlSeconds must be greater than zero.");
        }
    }

    internal bool IsEnabled => _options.UsePostgreSqlForAgents;

    internal async Task<AgentConfigEntity?> GetAuthoritativeAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        return await GetRepository().GetAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AgentConfigEntity>> ListAuthoritativeAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        return await GetRepository().ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AgentConfigEntity?> GetRuntimeAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_redis.IsAvailable)
        {
            try
            {
                AgentConfigEntity? cached = await ReadCacheAsync(agentId).ConfigureAwait(false);
                if (cached != null)
                {
                    return cached;
                }
            }
            catch (Exception exception) when (exception is RedisException or JsonException)
            {
                EngineLog.AgentConfigCacheReadFailed(_logger, exception, agentId);
            }
        }

        AgentConfigEntity? entity = await GetRepository()
            .GetAsync(agentId, cancellationToken)
            .ConfigureAwait(false);
        if (entity == null)
        {
            return null;
        }

        EngineLog.AgentConfigLoadedFromPostgreSql(_logger, agentId, entity.CurrentVersion);
        await TryWriteCacheAsync(entity, publish: false, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    internal async Task<AgentConfigEntity?> SaveAsync(
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        string? effectiveVersion = string.IsNullOrWhiteSpace(expectedVersion)
            ? entity.CurrentVersion
            : expectedVersion;
        AgentConfigEntity? saved = await GetRepository()
            .UpsertAsync(agentId, entity, effectiveVersion, cancellationToken)
            .ConfigureAwait(false);
        if (saved == null)
        {
            return null;
        }

        AgentConfigEntity snapshotEntity = Clone(saved);
        ApplyTenant(snapshotEntity);
        _snapshot.SetFullConfig(agentId, snapshotEntity.Config);
        await TryWriteCacheAsync(saved, publish: true, CancellationToken.None).ConfigureAwait(false);
        return saved;
    }

    internal bool RefreshFromCache(string agentId)
    {
        if (!IsEnabled)
        {
            return false;
        }

        try
        {
            RedisValue value = _redis.StringGet(BuildCacheKey(agentId));
            if (value.IsNullOrEmpty)
            {
                _snapshot.Evict(agentId);
                return false;
            }

            AgentConfigEntity? entity = Deserialize(value.ToString());
            if (entity?.Config == null
                || !string.Equals(entity.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ApplyTenant(entity);
            _snapshot.SetFullConfig(agentId, entity.Config);
            EngineLog.AgentConfigPostgreSqlHotReloaded(
                _logger,
                agentId,
                entity.CurrentVersion);
            return true;
        }
        catch (Exception exception) when (exception is RedisException or JsonException)
        {
            EngineLog.AgentConfigCacheReadFailed(_logger, exception, agentId);
            return false;
        }
    }

    internal async Task<bool> TryWarmupAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled || !_redis.IsAvailable)
        {
            return !IsEnabled;
        }

        IReadOnlyList<AgentConfigEntity> agents = await GetRepository()
            .ListAsync(tenantId: null, cancellationToken)
            .ConfigureAwait(false);
        int cached = 0;
        foreach (AgentConfigEntity agent in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryWriteCacheAsync(agent, publish: false, cancellationToken).ConfigureAwait(false))
            {
                cached++;
            }
        }

        EngineLog.AgentConfigCacheWarmupCompleted(_logger, cached, agents.Count);
        return cached == agents.Count;
    }

    private async Task<AgentConfigEntity?> ReadCacheAsync(string agentId)
    {
        RedisValue value = await _redis
            .StringGetAsync(BuildCacheKey(agentId))
            .ConfigureAwait(false);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        AgentConfigEntity? entity = Deserialize(value.ToString());
        return MatchesAgent(entity, agentId) ? entity : null;
    }

    private async Task<bool> TryWriteCacheAsync(
        AgentConfigEntity entity,
        bool publish,
        CancellationToken cancellationToken)
    {
        if (!_redis.IsAvailable)
        {
            return false;
        }

        try
        {
            bool written = await WriteCacheAsync(entity, publish, cancellationToken).ConfigureAwait(false);
            if (!written)
            {
                EngineLog.AgentConfigCacheWriteRejected(_logger, entity.AgentId);
            }
            return written;
        }
        catch (Exception exception) when (exception is RedisException or InvalidOperationException)
        {
            EngineLog.AgentConfigCacheWriteFailed(_logger, exception, entity.AgentId);
            return false;
        }
    }

    private async Task<bool> WriteCacheAsync(
        AgentConfigEntity entity,
        bool publish,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string payload = JsonSerializer.Serialize(entity, JsonOptions);
        TimeSpan expiry = TimeSpan.FromSeconds(_options.RedisCacheTtlSeconds);
        if (!publish)
        {
            bool stored = await _redis.StringSetAsync(
                BuildCacheKey(entity.AgentId),
                payload,
                expiry).ConfigureAwait(false);
            if (stored)
            {
                await _redis.SetAddAsync(CacheIndexKey, entity.AgentId).ConfigureAwait(false);
            }
            return stored;
        }

        string notification = JsonSerializer.Serialize(new ConfigUpdate
        {
            ResourceType = ConfigUpdate.PostgreSqlAgentResourceType,
            ResourceId = entity.AgentId,
            Operation = ConfigUpdate.UpsertOperation,
            Version = entity.CurrentVersion,
            Timestamp = DateTimeOffset.UtcNow
        }, ConfigUpdateDispatcher.JsonOptions);
        ITransaction transaction = _redis.GetDatabase().CreateTransaction();
        Task<bool> setTask = transaction.StringSetAsync(
            BuildCacheKey(entity.AgentId),
            payload,
            expiry);
        Task<bool> indexTask = transaction.SetAddAsync(CacheIndexKey, entity.AgentId);
        Task<long> publishTask = transaction.PublishAsync(
            RedisChannel.Literal(HotReloadService.CurrentUpdatesChannel),
            notification);
        bool executed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!executed || !await setTask.ConfigureAwait(false))
        {
            return false;
        }

        await indexTask.ConfigureAwait(false);
        await publishTask.ConfigureAwait(false);
        return true;
    }

    private IAgentConfigRepository GetRepository() =>
        _repository ?? throw new InvalidOperationException(
            "ConfigurationStore:UsePostgreSqlForAgents requires IAgentConfigRepository.");

    private static AgentConfigEntity? Deserialize(string payload) =>
        JsonSerializer.Deserialize<AgentConfigEntity>(payload, JsonOptions);

    private static bool MatchesAgent(AgentConfigEntity? entity, string agentId) =>
        entity?.Config != null
        && string.Equals(entity.AgentId, agentId, StringComparison.OrdinalIgnoreCase);

    private static AgentConfigEntity Clone(AgentConfigEntity entity) =>
        Deserialize(JsonSerializer.Serialize(entity, JsonOptions))
        ?? throw new InvalidOperationException(
            $"Agent configuration '{entity.AgentId}' could not be cloned.");

    private static void ApplyTenant(AgentConfigEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.TenantId))
        {
            entity.Config.TenantId = entity.TenantId;
        }
    }

    private static string BuildCacheKey(string agentId) => $"{CacheKeyPrefix}{agentId}";
}
