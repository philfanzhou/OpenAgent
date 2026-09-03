using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure.Configuration;

internal sealed class LlmConfigRepository(
    IDbContextFactory<OpenAgentDbContext> contexts) : ILlmConfigRepository
{
    public async Task<LlmProviderProfile?> GetAsync(
        string tenantId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LlmConfigurationEntity? entity = await context.LlmConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.ProfileId == profileId,
                cancellationToken).ConfigureAwait(false);
        return entity == null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LlmConfigurationEntity[] entities = await context.LlmConfigurations
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .OrderBy(item => item.ProfileId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(Map).ToArray();
    }

    public async Task<LlmProviderProfile> UpsertAsync(
        string tenantId,
        string profileId,
        LlmProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LlmConfigurationEntity? entity = await context.LlmConfigurations
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.ProfileId == profileId,
                cancellationToken).ConfigureAwait(false);
        if (entity == null)
        {
            entity = new LlmConfigurationEntity { TenantId = tenantId, ProfileId = profileId };
            context.LlmConfigurations.Add(entity);
        }
        entity.Name = profile.Name;
        entity.Format = profile.Format;
        entity.ModelId = profile.ModelId;
        entity.Endpoint = profile.Endpoint;
        entity.ApiKey = profile.ApiKey;
        entity.Temperature = profile.Temperature;
        entity.ContextTokens = profile.ContextTokens;
        entity.MaxOutputTokens = profile.MaxOutputTokens;
        entity.SupportsMaxOutputTokens = profile.SupportsMaxOutputTokens;
        entity.Modality = profile.Modality;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<bool> DeleteAsync(
        string tenantId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        int deleted = await context.LlmConfigurations
            .Where(item => item.TenantId == tenantId && item.ProfileId == profileId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0;
    }

    private static LlmProviderProfile Map(LlmConfigurationEntity entity) => new()
    {
        TenantId = entity.TenantId,
        Id = entity.ProfileId,
        Name = entity.Name,
        Format = entity.Format,
        ModelId = entity.ModelId,
        Endpoint = entity.Endpoint,
        ApiKey = entity.ApiKey,
        Temperature = entity.Temperature,
        ContextTokens = entity.ContextTokens,
        MaxOutputTokens = entity.MaxOutputTokens,
        SupportsMaxOutputTokens = entity.SupportsMaxOutputTokens,
        Modality = entity.Modality
    };
}
