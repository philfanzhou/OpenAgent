using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Configuration;
using System.Text.Json.Serialization;

namespace OpenAgent.Engine.Config;

internal sealed class AgentListQuery(
    IRedisConnectionProvider redis,
    ILogger<AgentListQuery> logger,
    AgentConfigLocalStore localStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<IReadOnlyList<AgentSummary>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var result = new List<AgentSummary>();
        AddLocalAgents(result);
        if (!redis.IsAvailable)
        {
            EngineLog.ListAgentsRedisUnavailable(logger);
            return result;
        }

        try
        {
            var agentIds = await redis.SetMembersAsync("agent:published:index").ConfigureAwait(false);
            foreach (var agentId in agentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configJson = await redis.StringGetAsync($"agent:config:{agentId}").ConfigureAwait(false);
                if (configJson.IsNullOrEmpty)
                {
                    continue;
                }

                try
                {
                    var entity = JsonSerializer.Deserialize<AgentConfigEntity>(configJson.ToString(), JsonOptions);
                    if (entity != null)
                    {
                        AddAgent(result, entity);
                    }
                }
                catch (Exception exception)
                {
                    EngineLog.ListAgentsParseFailed(logger, exception, agentId, configJson.ToString().Length);
                }
            }

        }
        catch (Exception exception)
        {
            EngineLog.ListAgentsFailed(logger, exception);
        }

        return result;
    }

    private void AddLocalAgents(List<AgentSummary> result)
    {
        foreach (AgentConfigEntity entity in localStore.List())
        {
            AddAgent(result, entity);
        }
    }

    private static void AddAgent(List<AgentSummary> result, AgentConfigEntity entity)
    {
        result.RemoveAll(item => string.Equals(item.AgentId, entity.AgentId, StringComparison.OrdinalIgnoreCase));
        result.Add(new AgentSummary
        {
            AgentId = entity.AgentId,
            Name = entity.Name,
            Description = entity.Description,
            Status = (int)entity.Status,
            CurrentVersion = entity.CurrentVersion,
            ApiFormat = entity.Config?.Llm.Format.ToString() ?? "unknown"
        });
    }
}
