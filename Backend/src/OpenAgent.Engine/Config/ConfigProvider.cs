using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Config;

internal class ConfigProvider : IAgentConfigProvider
{
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IRedisConnectionProvider _redis;
    private readonly ILogger<ConfigProvider> _logger;
    private readonly ConfigSnapshot _snapshot;
    private readonly MockAgentResolver _mockAgentResolver;
    private readonly SecretInjector _secretInjector;
    private readonly AgentListQuery _agentListQuery;
    private readonly AgentConfigLocalStore _localStore;
    private readonly AgentConfigDatabaseStore? _databaseStore;

    public ConfigProvider(
        IRedisConnectionProvider redis,
        ILogger<ConfigProvider> logger,
        ConfigSnapshot snapshot,
        MockAgentResolver mockAgentResolver,
        SecretInjector secretInjector,
        AgentListQuery agentListQuery,
        AgentConfigLocalStore localStore,
        AgentConfigDatabaseStore? databaseStore = null)
    {
        _redis = redis;
        _logger = logger;
        _snapshot = snapshot;
        _mockAgentResolver = mockAgentResolver;
        _secretInjector = secretInjector;
        _agentListQuery = agentListQuery;
        _localStore = localStore;
        _databaseStore = databaseStore;
    }

    public Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromException<AgentConfig>(
            new InvalidOperationException(
                "GetConfigAsync() without agentId is not supported. Use GetConfigAsync(string agentId) instead."));
    }

    public async Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            if (_mockAgentResolver.IsEnabled)
            {
                EngineLog.ConfigMockFallback(_logger);
                var mockConfig = _mockAgentResolver.CreateFallback();
                return mockConfig;
            }

            EngineLog.ConfigMissingAgentIdDisabled(_logger);
            return null;
        }

        if (_mockAgentResolver.IsEnabled)
        {
            AgentConfigEntity? localEntity = _localStore.Get(agentId);
            if (localEntity?.Config != null)
            {
                ApplyTenant(localEntity);
                _secretInjector.Enrich(localEntity.Config);
                return localEntity.Config;
            }
        }

        var snapshotConfig = LoadFromSnapshot(agentId);
        if (snapshotConfig != null)
        {
            EngineLog.ConfigLoadedFromSnapshot(_logger, agentId);
            return snapshotConfig;
        }

        if (_databaseStore?.IsEnabled == true)
        {
            AgentConfigEntity? databaseEntity = await _databaseStore
                .GetRuntimeAsync(agentId, cancellationToken)
                .ConfigureAwait(false);
            if (databaseEntity?.Config != null)
            {
                ApplyTenant(databaseEntity);
                _snapshot.SetFullConfig(agentId, databaseEntity.Config);
                _secretInjector.Enrich(databaseEntity.Config);
                return databaseEntity.Config;
            }

            return ResolveMissingConfig(agentId);
        }

        if (_redis.IsAvailable)
        {
            var redisConfigEntity = await LoadFromRedisAsync(agentId, cancellationToken);
            if (redisConfigEntity?.Config != null)
            {
                ApplyTenant(redisConfigEntity);
                _snapshot.SetFullConfig(agentId, redisConfigEntity.Config);
                EngineLog.ConfigLoadedFromRedisCached(_logger, agentId);
                return redisConfigEntity.Config;
            }
        }
        else
        {
            EngineLog.ConfigRedisIslandMode(_logger, agentId);
        }

        if (_mockAgentResolver.IsEnabled)
        {
            EngineLog.ConfigNotFoundDegradingToMock(_logger, agentId);
            var mockConfig = _mockAgentResolver.CreateFallback();
            _snapshot.SetFullConfig(agentId, mockConfig);
            return mockConfig;
        }

        EngineLog.ConfigNotCached(_logger, agentId);
        return null;
    }

    public async Task<AgentConfig?> GetConfigAsync(
        string agentId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return _mockAgentResolver.IsEnabled ? CreateFallback(tenantId) : null;
        }

        if (_mockAgentResolver.IsEnabled)
        {
            AgentConfigEntity? localEntity = _localStore.Get(agentId);
            if (localEntity?.Config != null)
            {
                ApplyTenant(localEntity);
                return ResolveForTenant(localEntity.Config, tenantId);
            }
        }

        AgentConfig? snapshotConfig = LoadFromSnapshot(agentId);
        if (snapshotConfig != null)
        {
            return ResolveForTenant(snapshotConfig, tenantId);
        }

        if (_databaseStore?.IsEnabled == true)
        {
            AgentConfigEntity? databaseEntity = await _databaseStore
                .GetRuntimeAsync(agentId, cancellationToken)
                .ConfigureAwait(false);
            if (databaseEntity?.Config == null)
            {
                return _mockAgentResolver.IsEnabled ? CreateFallback(tenantId) : null;
            }

            ApplyTenant(databaseEntity);
            AgentConfig? config = ResolveForTenant(databaseEntity.Config, tenantId);
            if (config != null)
            {
                _snapshot.SetFullConfig(agentId, config);
            }
            return config;
        }

        if (_redis.IsAvailable)
        {
            AgentConfigEntity? redisEntity = await LoadFromRedisAsync(
                agentId,
                cancellationToken).ConfigureAwait(false);
            if (redisEntity?.Config != null)
            {
                ApplyTenant(redisEntity);
                AgentConfig? config = ResolveForTenant(redisEntity.Config, tenantId);
                if (config != null)
                {
                    _snapshot.SetFullConfig(agentId, config);
                }
                return config;
            }
        }

        return _mockAgentResolver.IsEnabled ? CreateFallback(tenantId) : null;
    }

    private AgentConfig? LoadFromSnapshot(string agentId)
    {
        try
        {
            if (_snapshot.TryGetConfig<AgentConfig>(agentId, "FullAgentConfig", out var fullConfig) && fullConfig != null)
            {
                _secretInjector.Enrich(fullConfig);
                return fullConfig;
            }

            var config = new AgentConfig();
            bool hasAnyConfig = false;

            if (_snapshot.TryGetConfig<LlmConfig>(agentId, "LLMSettings", out var llmConfig) && llmConfig != null)
            {
                config.Llm = llmConfig;
                hasAnyConfig = true;
            }

            if (_snapshot.TryGetConfig<RagConfig>(agentId, "RAGSettings", out var ragConfig) && ragConfig != null)
            {
                config.Rag = ragConfig;
                hasAnyConfig = true;
            }

            if (_snapshot.TryGetConfig<McpConfig>(agentId, "MCPSettings", out var mcpConfig) && mcpConfig != null)
            {
                config.Mcp = mcpConfig;
                hasAnyConfig = true;
            }

            if (_snapshot.TryGetConfig<SkillsConfig>(agentId, "SkillsSettings", out var skillsConfig) && skillsConfig != null)
            {
                config.Skills = skillsConfig;
                hasAnyConfig = true;
            }

            if (!hasAnyConfig)
            {
                return null;
            }

            _secretInjector.Enrich(config);
            return config;
        }
        catch (Exception ex)
        {
            EngineLog.ConfigSnapshotLoadFailed(_logger, ex, agentId);
            return null;
        }
    }

    private async Task<AgentConfigEntity?> LoadFromRedisAsync(string agentId, CancellationToken cancellationToken)
    {
        var configJson = await _redis.StringGetAsync($"agent:config:{agentId}");

        if (configJson.IsNullOrEmpty)
        {
            EngineLog.ConfigNotFoundInRedis(_logger, agentId);
            return null;
        }

        try
        {
            var entity = JsonSerializer.Deserialize<AgentConfigEntity>(configJson.ToString(), CaseInsensitiveJsonOptions);

            if (entity?.Config != null)
            {
                EngineLog.ConfigLoadedFromRedisDetails(_logger, entity.AgentId, entity.CurrentVersion, entity.Config.Llm.Format.ToString(), configJson.ToString().Length);

                return entity;
            }

            EngineLog.ConfigPayloadInvalid(_logger, agentId, configJson.ToString().Length);
        }
        catch (Exception ex)
        {
            EngineLog.ConfigDeserializeFailed(_logger, ex, agentId, configJson.ToString().Length);
        }

        return null;
    }

    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        return await _agentListQuery.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        return await _agentListQuery.ExecuteAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyTenant(AgentConfigEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.TenantId))
        {
            entity.Config.TenantId = entity.TenantId;
        }
    }

    private AgentConfig CreateFallback(string tenantId)
    {
        AgentConfig config = _mockAgentResolver.CreateFallback();
        config.TenantId = tenantId;
        return config;
    }

    private AgentConfig? ResolveForTenant(AgentConfig config, string tenantId)
    {
        // 调试用：空租户（存量）配置视为全局可见。
        if (!string.IsNullOrWhiteSpace(config.TenantId)
            && !string.Equals(config.TenantId, tenantId, StringComparison.Ordinal))
        {
            return null;
        }

        _secretInjector.Enrich(config);
        return config;
    }

    private AgentConfig? ResolveMissingConfig(string agentId)
    {
        if (_mockAgentResolver.IsEnabled)
        {
            EngineLog.ConfigNotFoundDegradingToMock(_logger, agentId);
            AgentConfig mockConfig = _mockAgentResolver.CreateFallback();
            _snapshot.SetFullConfig(agentId, mockConfig);
            return mockConfig;
        }

        EngineLog.ConfigNotCached(_logger, agentId);
        return null;
    }
}
