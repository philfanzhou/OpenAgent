using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Engine.Abstractions;

internal interface ISkillCatalogStore : ISkillCatalog
{
    Task PublishAsync(SkillInstanceConfig skill, CancellationToken cancellationToken = default);
    Task RemoveAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default);
}
