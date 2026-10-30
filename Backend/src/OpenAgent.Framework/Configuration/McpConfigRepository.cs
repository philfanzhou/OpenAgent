using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Configuration;

internal sealed class McpConfigRepository : IMcpConfigRepository
{
    private readonly IDbContextFactory<OpenAgentDbContext> contexts;
    private readonly ConfigurationInvalidation? _invalidation;

    public McpConfigRepository(IDbContextFactory<OpenAgentDbContext> contexts, ConfigurationInvalidation? invalidation = null)
    {
        this.contexts = contexts;
        _invalidation = invalidation;
        if (invalidation is not null) invalidation.Changed += (kind, tenant, id) =>
        {
            if (kind == "mcp") _cache.Invalidate(tenant, id);
        };
    }

    // 仓储注册为单例：轮次间复用读缓存（含负结果），写入/删除即时失效。
    private readonly TenantEntryCache _cache = new();

    public Task<McpServerConfig?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default)
    {
        return _cache.GetOrAddAsync(tenantId, id, ct => ReadAsync(tenantId, id, ct), cancellationToken);
    }

    public Task<McpServerConfig?> GetFreshAsync(string tenantId, string id, CancellationToken cancellationToken = default)
        => ReadAsync(tenantId, id, cancellationToken);

    private async Task<McpServerConfig?> ReadAsync(string tenantId, string id, CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        McpConfigurationEntity? entity = await context.McpConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Name == id, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : JsonSerializer.Deserialize<McpServerConfig>(entity.DefinitionJson);
    }

    public async Task<IReadOnlyList<McpServerConfig>> ListAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        string[] definitions = await context.McpConfigurations.AsNoTracking().Where(item => item.TenantId == tenantId)
            .OrderBy(item => item.Name).Select(item => item.DefinitionJson).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return definitions.Select(json => JsonSerializer.Deserialize<McpServerConfig>(json)!).ToArray();
    }

    public async Task SaveAsync(string tenantId, McpServerConfig server, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        McpConfigurationEntity? entity = await context.McpConfigurations
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Name == server.Name, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new McpConfigurationEntity { TenantId = tenantId, Name = server.Name };
            context.McpConfigurations.Add(entity);
        }
        server.TenantId = tenantId;
        entity.DefinitionJson = JsonSerializer.Serialize(server);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _cache.Invalidate(tenantId, server.Name);
        if (_invalidation is not null) await _invalidation.PublishAsync("mcp", tenantId, server.Name).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        bool deleted = await context.McpConfigurations.Where(item => item.TenantId == tenantId && item.Name == id)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
        _cache.Invalidate(tenantId, id);
        if (_invalidation is not null) await _invalidation.PublishAsync("mcp", tenantId, id).ConfigureAwait(false);
        return deleted;
    }
}
