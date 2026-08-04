using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Engine.Reload;

internal sealed class FullConfigRefresher
{
    private readonly IRedisConnectionProvider _redis;
    private readonly ConfigSnapshot _snapshot;
    private readonly ILogger<FullConfigRefresher> _logger;

    public FullConfigRefresher(
        IRedisConnectionProvider redis,
        ConfigSnapshot snapshot,
        ILogger<FullConfigRefresher> logger)
    {
        _redis = redis;
        _snapshot = snapshot;
        _logger = logger;
    }

    internal bool Refresh(string agentId)
    {
        var configJson = _redis.StringGet($"agent:config:{agentId}");
        if (configJson.IsNullOrEmpty)
        {
            EngineLog.HotReloadRefreshNoConfig(_logger, agentId);
            _snapshot.Evict(agentId);
            return false;
        }

        try
        {
            var entity = JsonSerializer.Deserialize<AgentConfigEntity>(
                configJson.ToString(), ConfigUpdateDispatcher.JsonOptions);
            if (entity?.Config == null)
            {
                EngineLog.HotReloadRefreshInvalidPayload(_logger, agentId);
                return false;
            }

            _snapshot.SetFullConfig(agentId, entity.Config);
            EngineLog.HotReloadRefreshCompleted(_logger, agentId, entity.CurrentVersion);
            return true;
        }
        catch (Exception exception)
        {
            EngineLog.HotReloadRefreshFailed(_logger, exception, agentId);
            return false;
        }
    }
}
