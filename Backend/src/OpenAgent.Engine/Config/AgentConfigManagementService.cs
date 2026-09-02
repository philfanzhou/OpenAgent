using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Config;

internal sealed class AgentConfigManagementService(AgentConfigDatabaseStore store)
{
    internal Task<AgentConfigEntity?> GetAsync(
        string agentId,
        string tenantId,
        CancellationToken cancellationToken = default) =>
        store.GetAuthoritativeAsync(tenantId, agentId, cancellationToken);

    internal Task<AgentConfigEntity?> SaveAsync(
        string agentId,
        string tenantId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        entity.AgentId = agentId;
        entity.TenantId = tenantId;
        entity.Config.TenantId = tenantId;
        foreach (McpServerConfig server in entity.Config.Mcp.Servers)
        {
            server.TenantId = tenantId;
        }
        foreach (RagInstanceConfig rag in entity.Config.Rag.Instances)
        {
            rag.AllowedTenantIds = [tenantId];
        }
        foreach (SkillInstanceConfig skill in entity.Config.Skills.Instances)
        {
            skill.TenantId = tenantId;
            skill.AllowedTenantIds = [tenantId];
        }
        return store.SaveAsync(
            tenantId,
            agentId,
            entity,
            expectedVersion,
            cancellationToken);
    }
}
