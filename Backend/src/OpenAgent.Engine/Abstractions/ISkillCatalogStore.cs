using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Engine.Abstractions;

internal interface ISkillCatalogStore : ISkillCatalog
{
    Task PublishAsync(SkillInstanceConfig skill, CancellationToken cancellationToken = default);
    Task RemoveAsync(
        string tenantId,
        string skillId,
        string type,
        CancellationToken cancellationToken = default);
}
