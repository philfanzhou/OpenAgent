using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class SkillCatalog : ISkillCatalog
{
    private readonly Dictionary<string, SkillInstanceConfig> _skills = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SkillInstanceConfig> skills = _skills.Values
            .Where(skill => string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
                && string.Equals(skill.Type, SkillTypes.AgentSkill, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
        return Task.FromResult(skills);
    }

    public Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _skills.TryGetValue(BuildKey(tenantId, skillId), out SkillInstanceConfig? skill);
        return Task.FromResult(skill);
    }

    internal void Register(SkillInstanceConfig skill)
    {
        if (!string.IsNullOrWhiteSpace(skill.TenantId)
            && !string.IsNullOrWhiteSpace(skill.Id)
            && string.Equals(skill.Type, SkillTypes.AgentSkill, StringComparison.OrdinalIgnoreCase))
        {
            _skills[BuildKey(skill.TenantId, skill.Id)] = skill;
        }
    }

    internal bool Remove(string tenantId, string skillId) =>
        _skills.Remove(BuildKey(tenantId, skillId));

    private static string BuildKey(string tenantId, string skillId) =>
        $"{tenantId}\n{skillId}";
}
