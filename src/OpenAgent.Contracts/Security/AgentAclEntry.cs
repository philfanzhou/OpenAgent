namespace OpenAgent.Contracts.Security;

public class AgentAclEntry
{
    public string AgentId { get; set; } = string.Empty;
    public List<string> AllowedUserIds { get; set; } = new();
    public List<string> AllowedGroups { get; set; } = new();
    public List<string> AllowedTenantIds { get; set; } = new();
    public List<string> AllowedRoles { get; set; } = new();
}
