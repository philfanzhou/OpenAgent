using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files;

internal sealed class UnconfiguredFileShareRepository : IFileShareRepository
{
    public Task CreateAsync(FileShareLinkRecord record, CancellationToken cancellationToken) =>
        Task.FromException(CreateException());

    public Task<FileShareLinkRecord?> GetAsync(string shareIdHash, CancellationToken cancellationToken) =>
        Task.FromException<FileShareLinkRecord?>(CreateException());

    public Task<bool> TryRedeemAsync(string shareIdHash, CancellationToken cancellationToken) =>
        Task.FromException<bool>(CreateException());

    public Task<IReadOnlyList<FileShareLinkRecord>> ListByOwnerAsync(
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<FileShareLinkRecord>>(CreateException());

    public Task<bool> TryRevokeAsync(
        string shareIdHash,
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken) =>
        Task.FromException<bool>(CreateException());

    private static AgentException CreateException() => new(
        AgentErrorCode.DependencyUnavailable,
        "File share persistence is not configured.");
}
