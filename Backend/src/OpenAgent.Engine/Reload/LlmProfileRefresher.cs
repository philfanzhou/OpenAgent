using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Reload;

internal sealed class LlmProfileRefresher
{
    private readonly IRedisConnectionProvider _redis;
    private readonly ILlmRegistry _registry;
    private readonly ILogger<LlmProfileRefresher> _logger;

    public LlmProfileRefresher(
        IRedisConnectionProvider redis,
        ILlmRegistry registry,
        ILogger<LlmProfileRefresher> logger)
    {
        _redis = redis;
        _registry = registry;
        _logger = logger;
    }

    internal bool Refresh(string profileId)
    {
        try
        {
            var profileJson = _redis.StringGet($"llm:registry:{profileId}");
            if (profileJson.IsNullOrEmpty)
            {
                _registry.Remove(profileId);
                EngineLog.HotReloadLlmProfileRemoved(_logger, profileId);
                return true;
            }

            LlmProviderProfile? profile = JsonSerializer.Deserialize<LlmProviderProfile>(
                profileJson.ToString(),
                ConfigUpdateDispatcher.JsonOptions);
            if (profile == null || string.IsNullOrWhiteSpace(profile.Id))
            {
                EngineLog.HotReloadLlmProfileInvalid(_logger, profileId);
                return false;
            }

            _registry.Register(profile);
            EngineLog.HotReloadLlmProfileRefreshed(_logger, profileId);
            return true;
        }
        catch (Exception exception)
        {
            EngineLog.HotReloadLlmProfileRefreshFailed(_logger, exception, profileId);
            return false;
        }
    }
}
