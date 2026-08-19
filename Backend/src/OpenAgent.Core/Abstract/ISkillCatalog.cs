using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Abstract;

/// <summary>
/// Available Skill metadata loaded from the platform catalog.
/// It is not an Agent binding; bindings come from AgentConfig.Skills.
/// </summary>
public interface ISkillCatalog
{
    Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        string? type = null,
        CancellationToken cancellationToken = default);

    Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        string type,
        CancellationToken cancellationToken = default);
}
