using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Engine.Config;

internal sealed class ConfigProvider(AgentConfigDatabaseStore store) : IAgentConfigProvider
{
    public async Task<AgentConfig?> GetConfigAsync(
        string agentId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        AgentConfigEntity? entity = await store
            .GetRuntimeAsync(tenantId, agentId, cancellationToken)
            .ConfigureAwait(false);
        return entity?.Config;
    }

    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AgentConfigEntity> entities = await store
            .ListAuthoritativeAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
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
}
