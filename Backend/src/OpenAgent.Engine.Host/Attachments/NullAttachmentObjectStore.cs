using OpenAgent.Contracts.Content;

namespace OpenAgent.Engine.Host.Attachments;

internal sealed class NullAttachmentObjectStore : IAttachmentObjectStore
{
    public Task<AttachmentObjectReference?> StoreAsync(
        AttachmentObjectUpload upload,
        Stream content,
        CancellationToken cancellationToken) =>
        Task.FromResult<AttachmentObjectReference?>(null);

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
