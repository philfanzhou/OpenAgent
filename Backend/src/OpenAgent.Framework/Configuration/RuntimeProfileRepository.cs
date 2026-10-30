using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Configuration;

internal sealed class RuntimeProfileRepository : IRuntimeProfileRepository
{
    private readonly IDbContextFactory<OpenAgentDbContext> contexts;
    private readonly ConfigurationInvalidation? _invalidation;

    public RuntimeProfileRepository(IDbContextFactory<OpenAgentDbContext> contexts, ConfigurationInvalidation? invalidation = null)
    {
        this.contexts = contexts;
        _invalidation = invalidation;
        if (invalidation is not null) invalidation.Changed += (kind, tenant, id) =>
        {
            if (kind == "runtime") _cache.Invalidate(tenant, id);
        };
    }

    // 仓储注册为单例：轮次间复用读缓存（含负结果），写入/删除即时失效。
    private readonly TenantEntryCache _cache = new();

    public Task<RuntimeProfile?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default)
    {
        return _cache.GetOrAddAsync(tenantId, id, ct => ReadAsync(tenantId, id, ct), cancellationToken);
    }

    private async Task<RuntimeProfile?> ReadAsync(string tenantId, string id, CancellationToken cancellationToken)
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
        _cache.Invalidate(tenantId, profile.Id);
        if (_invalidation is not null) await _invalidation.PublishAsync("runtime", tenantId, profile.Id).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        bool deleted = await context.RuntimeConfigurations.Where(item => item.TenantId == tenantId && item.RuntimeId == id)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
        _cache.Invalidate(tenantId, id);
        if (_invalidation is not null) await _invalidation.PublishAsync("runtime", tenantId, id).ConfigureAwait(false);
        return deleted;
    }
}
