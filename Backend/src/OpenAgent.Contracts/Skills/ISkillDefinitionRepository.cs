using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Skills;

public interface ISkillDefinitionRepository
{
    Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        SkillInstanceConfig skill,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default);
}
