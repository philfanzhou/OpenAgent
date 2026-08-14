namespace OpenAgent.Contracts.Files;

public interface IFileAssetService
{
    Task<FileAsset> UploadAsync(
        FileAssetCreateRequest request,
        Stream content,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken);

    /// <summary>
    /// 在当前请求范围内幂等地建立会话对文件的引用，供后续读取校验。只关联属于该租户和用户的文件。
    /// </summary>
    Task EnsureReferencesAsync(
        IReadOnlyList<string> fileIds,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    Task<FileAssetContent> ReadAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    Task<string> ReadTextAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken);

}
