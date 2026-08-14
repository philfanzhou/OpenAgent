using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class SkillCatalog : ISkillCatalog
{
    private readonly Dictionary<string, SkillInstanceConfig> _skills = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SkillInstanceConfig> GetAll() => [.. _skills.Values];

    public void Register(SkillInstanceConfig skill)
    {
        if (!string.IsNullOrWhiteSpace(skill.Id))
            _skills[skill.Id] = skill;
    }
}
