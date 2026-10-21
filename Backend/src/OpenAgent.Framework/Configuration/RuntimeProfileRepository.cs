using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Configuration;

internal sealed class RuntimeProfileRepository(IDbContextFactory<OpenAgentDbContext> contexts) : IRuntimeProfileRepository
{
    public async Task<RuntimeProfile?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        RuntimeConfigurationEntity? entity = await context.RuntimeConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.RuntimeId == id, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : JsonSerializer.Deserialize<RuntimeProfile>(entity.DefinitionJson);
    }

    public async Task<IReadOnlyList<RuntimeProfile>> ListAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        string[] definitions = await context.RuntimeConfigurations.AsNoTracking().Where(item => item.TenantId == tenantId)
            .OrderBy(item => item.RuntimeId).Select(item => item.DefinitionJson).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return definitions.Select(json => JsonSerializer.Deserialize<RuntimeProfile>(json)!).ToArray();
    }

    public async Task SaveAsync(string tenantId, RuntimeProfile profile, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        RuntimeConfigurationEntity? entity = await context.RuntimeConfigurations
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.RuntimeId == profile.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new RuntimeConfigurationEntity { TenantId = tenantId, RuntimeId = profile.Id };
            context.RuntimeConfigurations.Add(entity);
        }
        entity.DefinitionJson = JsonSerializer.Serialize(profile);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.RuntimeConfigurations.Where(item => item.TenantId == tenantId && item.RuntimeId == id)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
