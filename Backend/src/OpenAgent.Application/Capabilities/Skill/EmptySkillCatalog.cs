using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Application.Capabilities.Skill;

/// <summary>
/// 无目录宿主的默认空目录。宿主应注册自己的 ISkillCatalog；
/// Engine 以 PostgreSQL 仓储为事实源（SkillCatalogStore）。
/// </summary>
internal sealed class EmptySkillCatalog : ISkillCatalog
{
    public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SkillInstanceConfig>>([]);

    public Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<SkillInstanceConfig?>(null);
}
