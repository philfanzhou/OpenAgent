using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure.Configuration;

internal sealed class EfCoreLlmConfigRepository(
    IDbContextFactory<OpenAgentDbContext> contexts) : ILlmConfigRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

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
        return entity == null ? null : Deserialize(entity.ConfigurationJson, tenantId, profileId);
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
        return entities.Select(entity => Deserialize(
            entity.ConfigurationJson,
            entity.TenantId,
            entity.ProfileId)).ToArray();
    }

    public async Task<LlmProviderProfile> UpsertAsync(
        string tenantId,
        string profileId,
        LlmProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        LlmProviderProfile persisted = Clone(profile);
        persisted.TenantId = tenantId;
        persisted.Id = profileId;
        string json = JsonSerializer.Serialize(persisted, JsonOptions);

        await using OpenAgentDbContext context = await contexts
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LlmConfigurationEntity? entity = await context.LlmConfigurations
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.ProfileId == profileId,
                cancellationToken).ConfigureAwait(false);
        if (entity == null)
        {
            context.LlmConfigurations.Add(new LlmConfigurationEntity
            {
                TenantId = tenantId,
                ProfileId = profileId,
                ConfigurationJson = json,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            entity.ConfigurationJson = json;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return persisted;
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

    private static LlmProviderProfile Deserialize(string json, string tenantId, string profileId)
    {
        LlmProviderProfile profile = JsonSerializer.Deserialize<LlmProviderProfile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"LLM profile '{profileId}' is invalid.");
        profile.TenantId = tenantId;
        profile.Id = profileId;
        return profile;
    }

    private static LlmProviderProfile Clone(LlmProviderProfile profile) =>
        JsonSerializer.Deserialize<LlmProviderProfile>(
            JsonSerializer.Serialize(profile, JsonOptions),
            JsonOptions)!;
}
