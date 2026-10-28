namespace OpenAgent.Contracts.Files;

public interface IFileObjectStore
{
    Task<FileObjectReference> WriteAsync(
        FileObjectWriteRequest request,
        Stream content,
        CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>
    /// Reads an object without buffering more than <paramref name="maxBytes"/>.
    /// Implementations must reject oversized responses before returning any content.
    /// </summary>
    Task<byte[]> ReadAsync(
        string objectKey,
        long maxBytes,
        CancellationToken cancellationToken);

    Task<FileObjectAccessReference> CreateReadUrlAsync(
        string objectKey,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}
