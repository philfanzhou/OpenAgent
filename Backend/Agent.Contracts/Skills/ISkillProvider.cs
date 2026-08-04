using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Skills;

public interface ISkillProvider
{
    Task<IReadOnlyList<SkillDescriptor>> GetSkillDescriptorsAsync(
        string? agentId, IAgentUserContext? userContext, SkillsConfig? overrideConfig = null, CancellationToken cancellationToken = default);

    Task<string> ExecuteAsync(
        string skillName, Dictionary<string, object> arguments, IAgentUserContext? userContext, CancellationToken cancellationToken = default);

    void RegisterSkill(ISkill skill, SkillSource source = SkillSource.Local, string? sourceId = null);
}
