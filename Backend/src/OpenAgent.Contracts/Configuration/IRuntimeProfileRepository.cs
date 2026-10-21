namespace OpenAgent.Contracts.Configuration;

public interface IRuntimeProfileRepository
{
    Task<RuntimeProfile?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeProfile>> ListAsync(string tenantId, CancellationToken cancellationToken = default);
    Task SaveAsync(string tenantId, RuntimeProfile profile, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string tenantId, string id, CancellationToken cancellationToken = default);
}
