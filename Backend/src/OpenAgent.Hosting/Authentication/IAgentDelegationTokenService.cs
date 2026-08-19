namespace OpenAgent.Hosting.Authentication;

public interface IAgentDelegationTokenService
{
    string CreateToken(AgentDelegationIdentity identity);
}

public sealed record AgentDelegationIdentity(
    string UserId,
    string? TenantId,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, string> Claims,
    IReadOnlyList<string> Audience);
