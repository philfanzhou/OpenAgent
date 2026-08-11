namespace OpenAgent.Contracts.Files;

public interface IFileAssetService
{
    Task<FileAsset> UploadAsync(
        FileAssetCreateRequest request,
        Stream content,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken);

    Task<FileAssetContent> ReadAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    Task<string> ReadTextAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    Task AttachToConversationAsync(
        IReadOnlyList<string> fileIds,
        string? conversationId,
        CancellationToken cancellationToken);
}
