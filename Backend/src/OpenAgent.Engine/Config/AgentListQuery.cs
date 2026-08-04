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
    ILogger<AgentListQuery> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<IReadOnlyList<AgentSummary>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var result = new List<AgentSummary>();
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
                        result.Add(new AgentSummary
                        {
                            AgentId = entity.AgentId,
                            Name = entity.Name,
                            Status = (int)entity.Status,
                            CurrentVersion = entity.CurrentVersion,
                            ApiFormat = entity.Config?.Llm.Format.ToString() ?? "unknown"
                        });
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
}
