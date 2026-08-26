using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files;

internal sealed class UnconfiguredFileObjectStore : IFileObjectStore
{
    public Task<FileObjectReference> WriteAsync(
        FileObjectWriteRequest request,
        Stream content,
        CancellationToken cancellationToken) =>
        Task.FromException<FileObjectReference>(CreateException());

    public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromException<byte[]>(CreateException());

    public Task<byte[]> ReadAsync(
        string objectKey,
        long maxBytes,
        CancellationToken cancellationToken) =>
        Task.FromException<byte[]>(CreateException());

    public Task<FileObjectAccessReference> CreateReadUrlAsync(
        string objectKey,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken) =>
        Task.FromException<FileObjectAccessReference>(CreateException());

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromException(CreateException());

    private static AgentException CreateException() => new(
        AgentErrorCode.DependencyUnavailable,
        "File object storage is not configured.");
}
