namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// Protects and resolves tenant-scoped secret values at the persistence boundary.
/// </summary>
public interface IAgentSecretResolver
{
    Task<string> ProtectAsync(
        string tenantId,
        string secret,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveAsync(
        string tenantId,
        string protectedSecret,
        CancellationToken cancellationToken = default);
}
