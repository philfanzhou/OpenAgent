using OpenAgent.Contracts.Security;

namespace OpenAgent.Framework.Entities;

internal sealed class ResourcePolicyEntity
{
    public string TenantId { get; set; } = string.Empty;
    public AgentResourceType ResourceType { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string RulesJson { get; set; } = "[]";
}
