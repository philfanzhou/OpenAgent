using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Abstractions;

internal interface ISkillCatalogStore
{
    Task PublishAsync(SkillInstanceConfig skill, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(CancellationToken cancellationToken = default);
    Task RemoveAsync(string skillId, CancellationToken cancellationToken = default);
}
