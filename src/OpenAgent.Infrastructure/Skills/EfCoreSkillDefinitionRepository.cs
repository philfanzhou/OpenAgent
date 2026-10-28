using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure.Skills;

internal sealed class EfCoreSkillDefinitionRepository(
    IDbContextFactory<OpenAgentDbContext> contexts) : ISkillDefinitionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext database = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        SkillDefinitionEntity? entity = await database.SkillDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId
                    && item.SkillId == skillId
                    && item.Type == SkillTypes.AgentSkill,
                cancellationToken)
            .ConfigureAwait(false);
        return entity == null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext database = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        IQueryable<SkillDefinitionEntity> query = database.SkillDefinitions
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.Type == SkillTypes.AgentSkill);

        List<SkillDefinitionEntity> entities = await query
            .OrderBy(item => item.SkillId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(Map).ToList().AsReadOnly();
    }

    public async Task UpsertAsync(
        SkillInstanceConfig skill,
        CancellationToken cancellationToken = default)
    {
        Validate(skill);
        await using OpenAgentDbContext database = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        SkillDefinitionEntity? entity = await database.SkillDefinitions.FindAsync(
            [skill.TenantId, skill.Id, SkillTypes.AgentSkill],
            cancellationToken).ConfigureAwait(false);
        string payload = JsonSerializer.Serialize(skill, JsonOptions);
        if (entity == null)
        {
            database.SkillDefinitions.Add(new SkillDefinitionEntity
            {
                TenantId = skill.TenantId,
                SkillId = skill.Id,
                Type = skill.Type,
                SourceType = skill.SourceType,
                DefinitionJson = payload,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            entity.SourceType = skill.SourceType;
            entity.DefinitionJson = payload;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext database = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        int deleted = await database.SkillDefinitions
            .Where(item => item.TenantId == tenantId
                && item.SkillId == skillId
                && item.Type == SkillTypes.AgentSkill)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0;
    }

    private static SkillInstanceConfig Map(SkillDefinitionEntity entity)
    {
        SkillInstanceConfig skill = JsonSerializer.Deserialize<SkillInstanceConfig>(
            entity.DefinitionJson,
            JsonOptions) ?? throw new InvalidOperationException(
                $"Skill definition '{entity.SkillId}' is invalid.");
        skill.TenantId = entity.TenantId;
        skill.Id = entity.SkillId;
        skill.Type = entity.Type;
        skill.SourceType = entity.SourceType;
        return skill;
    }

    private static void Validate(SkillInstanceConfig skill)
    {
        if (string.IsNullOrWhiteSpace(skill.TenantId)
            || string.IsNullOrWhiteSpace(skill.Id)
            || !string.Equals(skill.Type, SkillTypes.AgentSkill, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(skill.SourceType))
        {
            throw new ArgumentException("Skill tenant, id, type and source type are required.", nameof(skill));
        }
    }
}
