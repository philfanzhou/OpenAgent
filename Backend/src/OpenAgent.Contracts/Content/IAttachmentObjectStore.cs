namespace OpenAgent.Contracts.Content;

public interface IAttachmentObjectStore
{
    Task<AttachmentObjectReference?> StoreAsync(
        AttachmentObjectUpload upload,
        Stream content,
        CancellationToken cancellationToken);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed record AttachmentObjectUpload(
    string FileName,
    string MediaType,
    string Sha256,
    string? TenantId);

public sealed record AttachmentObjectReference(
    string ObjectKey,
    string? ETag);
