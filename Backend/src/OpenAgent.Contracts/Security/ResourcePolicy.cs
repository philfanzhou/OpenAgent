namespace OpenAgent.Contracts.Security;

/// <summary>A tenant-local policy. Empty subjects match everyone; actions never default to wildcard.</summary>
public sealed record ResourcePolicyRule
{
    public bool Deny { get; init; }
    public string[] Actions { get; init; } = [];
    public string[] UserIds { get; init; } = [];
    public string[] Roles { get; init; } = [];
}

public sealed record ResourcePolicy
{
    public long Revision { get; init; }
    public ResourcePolicyRule[] Rules { get; init; } = [];
}

/// <summary>Authoritative resource existence and revision; never contains credentials.</summary>
public sealed record ResourceAccessSnapshot(bool Exists, string Revision, ResourcePolicy? Policy);

public interface IResourcePolicyStore
{
    Task<ResourceAccessSnapshot> ReadAsync(string tenantId, AgentAuthorizationRequest request, CancellationToken cancellationToken = default);
    // expectedRevision=0 creates; replacement is conditional. Empty rules revoke all access.
    Task<ResourcePolicy?> SaveAsync(string tenantId, AgentResourceType resourceType, string resourceId,
        ResourcePolicy policy, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>Shared authorization port for catalog and execution consumers.</summary>
public interface IResourceAuthorization
{
    Task<bool> IsAuthorizedAsync(AgentAuthorizationRequest request, IAgentUserContext userContext,
        CancellationToken cancellationToken = default);
}
