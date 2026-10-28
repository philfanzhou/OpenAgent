using OpenAgent.Router.Observability;
using StackExchange.Redis;

namespace OpenAgent.Router.Security;

internal sealed class AgentConfigAccessor(
    IConnectionMultiplexer? redis,
    ILogger<AgentVisibilityService> logger)
{
    internal async Task<List<string>> GetPublishedAgentIdsAsync(CancellationToken cancellationToken)
    {
        if (redis == null)
        {
            return [];
        }

        try
        {
            var members = await redis.GetDatabase().SetMembersAsync("agent:published:index");
            return members.Select(member => member.ToString()).ToList();
        }
        catch (Exception exception)
        {
            RouterLog.GetPublishedAgentIdsFailed(logger, exception);
            return [];
        }
    }

    internal async Task<string?> GetAgentConfigAsync(string agentId, CancellationToken cancellationToken)
    {
        if (redis == null)
        {
            return null;
        }

        try
        {
            var value = await redis.GetDatabase().StringGetAsync($"agent:config:{agentId}");
            return value.HasValue ? value.ToString() : null;
        }
        catch (Exception exception)
        {
            RouterLog.GetAgentConfigFailed(logger, exception, agentId);
            return null;
        }
    }
}
