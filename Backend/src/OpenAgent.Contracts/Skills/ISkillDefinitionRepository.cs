using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Skills;

public interface ISkillDefinitionRepository
{
    Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        string type,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        string? type = null,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        SkillInstanceConfig skill,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string tenantId,
        string skillId,
        string type,
        CancellationToken cancellationToken = default);
}
