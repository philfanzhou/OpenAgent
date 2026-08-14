using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Abstract;

/// <summary>
/// Available Skill metadata loaded from the platform catalog.
/// It is not an Agent binding; bindings come from AgentConfig.Skills.
/// </summary>
public interface ISkillCatalog
{
    IReadOnlyList<SkillInstanceConfig> GetAll();
    void Register(SkillInstanceConfig skill);
}
