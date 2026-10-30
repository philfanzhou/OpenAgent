using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Security;

internal sealed class ResourcePolicyStore(IDbContextFactory<OpenAgentDbContext> contexts) : IResourcePolicyStore
{
    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ResourceAccessSnapshot> ReadAsync(string tenantId, AgentAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        await using OpenAgentDbContext db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        string? revision = await ReadResourceAsync(db, tenantId, request, cancellationToken).ConfigureAwait(false);
        ResourcePolicyEntity? policy = await db.ResourcePolicies.AsNoTracking().SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.ResourceType == request.ResourceType && item.ResourceId == request.ResourceId,
            cancellationToken).ConfigureAwait(false);
        return new(revision is not null, revision ?? string.Empty, policy is null ? null : Map(policy));
    }

    public async Task<ResourcePolicy?> SaveAsync(string tenantId, AgentResourceType resourceType, string resourceId,
        ResourcePolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (tenantId.Length > 256 || resourceId.Length > 512 || !Enum.IsDefined(resourceType) || expectedRevision < 0
            || policy.Rules is null || policy.Rules.Length > 100 || policy.Rules.Any(rule => rule is null
                || rule.Actions is null || rule.Actions.Length == 0 || rule.Actions.Length > 16
                || rule.UserIds is null || rule.Roles is null || rule.UserIds.Length > 100 || rule.Roles.Length > 100
                || rule.Actions.Concat(rule.UserIds).Concat(rule.Roles).Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256)))
            throw new ArgumentException("Invalid resource policy.", nameof(policy));
        await using OpenAgentDbContext db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ResourcePolicyEntity? entity = await db.ResourcePolicies.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.ResourceType == resourceType && item.ResourceId == resourceId,
            cancellationToken).ConfigureAwait(false);
        if ((entity?.Revision ?? 0) != expectedRevision) return null;
        if (entity is null)
        {
            entity = new() { TenantId = tenantId, ResourceType = resourceType, ResourceId = resourceId };
            db.ResourcePolicies.Add(entity);
        }
        entity.Revision = checked(expectedRevision + 1);
        entity.RulesJson = JsonSerializer.Serialize(policy.Rules);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Map(entity);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { return null; }
    }

    private static ResourcePolicy Map(ResourcePolicyEntity entity) => new()
    {
        Revision = entity.Revision,
        Rules = JsonSerializer.Deserialize<ResourcePolicyRule[]>(entity.RulesJson)
            ?? throw new InvalidOperationException("Invalid persisted resource policy.")
    };

    private static async Task<string?> ReadResourceAsync(OpenAgentDbContext db, string tenantId,
        AgentAuthorizationRequest request, CancellationToken ct)
    {
        // Read the owning row without configuration caches. Hashes remain internal to the guard.
        object? resource = request.ResourceType switch
        {
            AgentResourceType.Agent => await db.AgentConfigurations.AsNoTracking().SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.AgentId == request.ResourceId, ct).ConfigureAwait(false),
            AgentResourceType.Model => await db.LlmConfigurations.AsNoTracking().SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.ProfileId == request.ResourceId, ct).ConfigureAwait(false),
            AgentResourceType.Mcp => await db.McpConfigurations.AsNoTracking().SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.Name == request.ResourceId, ct).ConfigureAwait(false),
            AgentResourceType.Skill => await db.SkillDefinitions.AsNoTracking().FirstOrDefaultAsync(
                item => item.TenantId == tenantId && item.SkillId == request.ResourceId && item.Type == SkillTypes.AgentSkill, ct).ConfigureAwait(false),
            // Platform tools are registered in process, with a tenant policy namespace. They cannot
            // select another tenant; their owning business service also checks object ownership.
            AgentResourceType.Tool or AgentResourceType.Function => "platform",
            _ => null
        };
        if (resource is null && !string.IsNullOrWhiteSpace(request.AgentId))
        {
            AgentConfigurationEntity? agent = await db.AgentConfigurations.AsNoTracking().SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.AgentId == request.AgentId, ct).ConfigureAwait(false);
            if (agent is not null)
            {
                bool embedded = request.ResourceType switch
                {
                    AgentResourceType.Model => JsonSerializer.Deserialize<OpenCodeConfig>(agent.OpenCodeJson, ConfigJson)?.Model is { } model
                        && request.ResourceId == "@agent/" + request.AgentId,
                    AgentResourceType.Mcp => (JsonSerializer.Deserialize<McpConfig>(agent.McpJson, ConfigJson)?.Servers ?? [])
                        .Any(item => item.Name == request.ResourceId && (item.TenantId == tenantId || string.IsNullOrEmpty(item.TenantId))),
                    AgentResourceType.Skill => (JsonSerializer.Deserialize<SkillsConfig>(agent.SkillsJson, ConfigJson)?.Instances ?? [])
                        .Any(item => item.Id == request.ResourceId && item.TenantId == tenantId),
                    _ => false
                };
                if (embedded) resource = agent;
            }
        }
        return resource is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resource))));
    }
}
