using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Config;

/// <summary>
/// 租户 Skill 目录的单一实现：以 PostgreSQL 仓储（ISkillDefinitionRepository）
/// 为唯一定义来源，不再维护 Redis 或进程内派生缓存。
/// </summary>
internal sealed class SkillCatalogStore(ISkillDefinitionRepository repository) : ISkillCatalogStore
{
    public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        return repository.ListAsync(tenantId, cancellationToken);
    }

    public Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, skillId);
        return repository.GetAsync(tenantId, skillId, cancellationToken);
    }

    public Task PublishAsync(SkillInstanceConfig skill, CancellationToken cancellationToken = default)
    {
        ValidateKey(skill.TenantId, skill.Id);
        return repository.UpsertAsync(skill, cancellationToken);
    }

    public Task RemoveAsync(string tenantId, string skillId, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, skillId);
        return repository.DeleteAsync(tenantId, skillId, cancellationToken);
    }

    private static void ValidateKey(string tenantId, string skillId)
    {
        ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("Skill id is required.", nameof(skillId));
        }
    }

    private static void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }
    }
}
