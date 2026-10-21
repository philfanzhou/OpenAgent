namespace OpenAgent.Contracts.Configuration;

public interface IMcpConfigRepository
{
    Task<McpServerConfig?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpServerConfig>> ListAsync(string tenantId, CancellationToken cancellationToken = default);
    Task SaveAsync(string tenantId, McpServerConfig server, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string tenantId, string id, CancellationToken cancellationToken = default);
}
