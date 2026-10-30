using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Tests.TestDoubles;

internal sealed class PolicyStore : IResourcePolicyStore
{
    internal ResourceAccessSnapshot Snapshot { get; set; } = new(true, "config-1", null);
    internal string Tenant { get; set; } = "tenant";
    internal int Reads { get; private set; }

    public Task<ResourceAccessSnapshot> ReadAsync(string tenantId, AgentAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        return Task.FromResult(tenantId == Tenant ? Snapshot : new(false, string.Empty, null));
    }

    public Task<ResourcePolicy?> SaveAsync(string tenantId, AgentResourceType type, string id,
        ResourcePolicy policy, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
