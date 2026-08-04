using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Skills;

public class SkillContext
{
    public required string SkillName { get; init; }
    public required Dictionary<string, object> Arguments { get; init; }
    public required IAgentUserContext UserContext { get; init; }
    public string? TraceId { get; init; }
    public string? TenantId { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public class SkillResult
{
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? ErrorMessage { get; init; }
    public Requests.AgentErrorCode? ErrorCode { get; init; }
}

public interface ISkill
{
    string Name { get; }
    string Description { get; }
    Task<string> ExecuteAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken = default);
}

public interface IAsyncSkill
{
    string Name { get; }
    string Description { get; }
    Task<SkillResult> ExecuteAsync(SkillContext context, CancellationToken cancellationToken);
}
