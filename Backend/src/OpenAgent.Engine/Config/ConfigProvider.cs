using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;
using OpenAgent.Engine.Config;

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

    public ConfigProvider(
        IRedisConnectionProvider redis,
        ILogger<ConfigProvider> logger,
        ConfigSnapshot snapshot,
        MockAgentResolver mockAgentResolver,
        SecretInjector secretInjector,
        AgentListQuery agentListQuery)
    {
        _redis = redis;
        _logger = logger;
        _snapshot = snapshot;
        _mockAgentResolver = mockAgentResolver;
        _secretInjector = secretInjector;
        _agentListQuery = agentListQuery;
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

        var snapshotConfig = LoadFromSnapshot(agentId);
        if (snapshotConfig != null)
        {
            EngineLog.ConfigLoadedFromSnapshot(_logger, agentId);
            return snapshotConfig;
        }

        if (_redis.IsAvailable)
        {
            var redisConfigEntity = await LoadFromRedisAsync(agentId, cancellationToken);
            if (redisConfigEntity?.Config != null)
            {
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
}
