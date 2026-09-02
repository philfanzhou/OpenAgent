namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// Resolves a tenant-relative secret reference for the execution path.
/// Configuration stores persist only the reference, never the returned value.
/// </summary>
public interface IAgentSecretResolver
{
    Task<string?> ResolveAsync(
        string tenantId,
        string secretReference,
        CancellationToken cancellationToken = default);
}
