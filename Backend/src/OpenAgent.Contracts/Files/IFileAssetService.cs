namespace OpenAgent.Contracts.Files;

public interface IFileAssetService
{
    Task<FileAsset> UploadAsync(
        FileAssetCreateRequest request,
        Stream content,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    Task<FileAsset?> GetAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    Task<FileObjectAccessReference> CreateTransferUrlAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// 获取当前会话已引用且可用的文件元数据，不读取对象存储内容。
    /// </summary>
    Task<FileAsset?> GetReferencedAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// 列出当前会话引用的文件资产。只返回当前租户和用户拥有的资产。
    /// </summary>
    Task<IReadOnlyList<FileAsset>> ListAsync(
        FileAssetScope scope,
        CancellationToken cancellationToken);

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
        CancellationToken cancellationToken,
        long? maxBytes = null);

    Task<string> ReadTextAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按对象存储键直读对象内容。键必须位于当前租户分区内，防止跨租户读取。
    /// 供模型工具读取 MCP 等外部组件直接写入对象存储的文件。
    /// </summary>
    Task<byte[]> ReadObjectAsync(
        string objectKey,
        FileAssetScope scope,
        CancellationToken cancellationToken,
        long? maxBytes = null);

    /// <summary>
    /// 按对象存储键直读 UTF-8 文本内容。与 <see cref="ReadTextAsync"/> 共享函数读取限额，
    /// 键必须位于当前租户分区内，防止跨租户读取。
    /// </summary>
    Task<string> ReadObjectTextAsync(
        string objectKey,
        FileAssetScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// 将多个文件（fileId 或 objectKey 定位）打包为 zip 并登记为当前租户/用户的新 FileAsset。
    /// 是否关联到 assistant 消息由 publish_files 能力决定。输入条目数与总字节数受 FileAssets 档案限额约束。
    /// </summary>
    Task<FileArchiveResult> CompressAsync(
        FileArchiveRequest request,
        FileAssetScope scope,
        CancellationToken cancellationToken);

}
