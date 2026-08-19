using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class SkillCatalog : ISkillCatalog
{
    private readonly Dictionary<string, SkillInstanceConfig> _skills = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SkillInstanceConfig> skills = _skills.Values
            .Where(skill => string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
                && (type == null || string.Equals(skill.Type, type, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();
        return Task.FromResult(skills);
    }

    public Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        string type,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _skills.TryGetValue(BuildKey(tenantId, skillId, type), out SkillInstanceConfig? skill);
        return Task.FromResult(skill);
    }

    internal void Register(SkillInstanceConfig skill)
    {
        if (!string.IsNullOrWhiteSpace(skill.TenantId)
            && !string.IsNullOrWhiteSpace(skill.Id)
            && !string.IsNullOrWhiteSpace(skill.Type))
        {
            _skills[BuildKey(skill.TenantId, skill.Id, skill.Type)] = skill;
        }
    }

    internal bool Remove(string tenantId, string skillId, string type) =>
        _skills.Remove(BuildKey(tenantId, skillId, type));

    private static string BuildKey(string tenantId, string skillId, string type) =>
        $"{tenantId}\n{type}\n{skillId}";
}
