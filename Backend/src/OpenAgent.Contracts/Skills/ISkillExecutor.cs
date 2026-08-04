using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Skills;

public interface ISkillExecutor
{
    string Name { get; }
    string Description { get; }
    string ParametersJsonSchema { get; }
    Task<string> ExecuteAsync(string toolName, Dictionary<string, object> arguments, IAgentUserContext? userContext = null, CancellationToken cancellationToken = default);
}
