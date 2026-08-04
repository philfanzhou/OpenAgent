namespace OpenAgent.Contracts.Security;

public class M2MAuthRule
{
    public string RuleId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public List<string> AllowedAgentIds { get; set; } = new();
}
