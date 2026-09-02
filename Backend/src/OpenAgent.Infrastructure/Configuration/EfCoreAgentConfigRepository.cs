using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure.Configuration;

internal sealed class EfCoreAgentConfigRepository(
    IDbContextFactory<OpenAgentDbContext> contexts) : IAgentConfigRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<AgentConfigEntity?> GetAsync(
        string tenantId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantId(tenantId);
        ValidateAgentId(agentId);
        await using OpenAgentDbContext database = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        AgentConfigurationEntity? entity = await database.AgentConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.AgentId == agentId,
                cancellationToken)
            .ConfigureAwait(false);
        return entity == null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext database = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        IQueryable<AgentConfigurationEntity> query = database.AgentConfigurations.AsNoTracking();
        if (tenantId != null)
        {
            query = query.Where(item => item.TenantId == tenantId);
        }

        List<AgentConfigurationEntity> entities = await query
            .OrderBy(item => item.AgentId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(Map).ToList().AsReadOnly();
    }

    public async Task<AgentConfigEntity?> UpsertAsync(
        string tenantId,
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantId(tenantId);
        ValidateAgentId(agentId);
        ArgumentNullException.ThrowIfNull(entity);
        ValidateNoInlineSecrets(entity);
        string entityTenantId = ResolveTenant(entity);
        if (!string.IsNullOrWhiteSpace(entityTenantId)
            && !string.Equals(entityTenantId, tenantId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Agent tenant id does not match the storage scope.", nameof(entity));
        }

        await using OpenAgentDbContext database = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        AgentConfigurationEntity? current = await database.AgentConfigurations
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.AgentId == agentId,
                cancellationToken)
            .ConfigureAwait(false);

        string? currentVersion = current?.Version.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(expectedVersion)
            && !string.Equals(currentVersion, expectedVersion, StringComparison.Ordinal))
        {
            return null;
        }

        long nextVersion = current == null ? 1 : checked(current.Version + 1);
        Stamp(entity, agentId, tenantId, nextVersion);
        string payload = JsonSerializer.Serialize(entity, JsonOptions);
        if (current == null)
        {
            database.AgentConfigurations.Add(new AgentConfigurationEntity
            {
                AgentId = agentId,
                TenantId = tenantId,
                ConfigurationJson = payload,
                Version = nextVersion,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            current.TenantId = tenantId;
            current.ConfigurationJson = payload;
            current.Version = nextVersion;
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Clone(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException postgres
            && postgres.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }

    private static AgentConfigEntity Map(AgentConfigurationEntity entity)
    {
        AgentConfigEntity config = JsonSerializer.Deserialize<AgentConfigEntity>(
            entity.ConfigurationJson,
            JsonOptions) ?? throw new InvalidOperationException(
                $"Agent configuration '{entity.AgentId}' is invalid.");
        ValidateNoInlineSecrets(config);
        Stamp(config, entity.AgentId, entity.TenantId, entity.Version);
        return config;
    }

    private static AgentConfigEntity Clone(AgentConfigEntity entity) =>
        JsonSerializer.Deserialize<AgentConfigEntity>(
            JsonSerializer.Serialize(entity, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException(
                $"Agent configuration '{entity.AgentId}' could not be cloned.");

    private static void Stamp(
        AgentConfigEntity entity,
        string agentId,
        string tenantId,
        long version)
    {
        entity.AgentId = agentId;
        entity.TenantId = tenantId;
        entity.Config.TenantId = tenantId;
        entity.CurrentVersion = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ResolveTenant(AgentConfigEntity entity) =>
        string.IsNullOrWhiteSpace(entity.TenantId)
            ? entity.Config.TenantId
            : entity.TenantId;

    private static void ValidateAgentId(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }
    }

    private static void ValidateTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }
    }

    private static void ValidateNoInlineSecrets(AgentConfigEntity entity)
    {
        if (entity.Config.Rag.Instances.Any(instance =>
                !string.IsNullOrWhiteSpace(instance.ApiKey)))
        {
            throw new ArgumentException(
                "Agent configuration cannot persist inline API keys. Use ApiKeySecretRef.",
                nameof(entity));
        }
    }
}
