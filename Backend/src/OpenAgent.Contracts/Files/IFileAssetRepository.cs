namespace OpenAgent.Contracts.Files;

public interface IFileAssetRepository
{
    Task CreateAsync(FileAsset asset, CancellationToken cancellationToken);
    Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken);
    Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken);

    /// <summary>
    /// 列出会话已引用的文件资产。实现必须按会话引用过滤，调用方再执行租户/用户边界校验。
    /// </summary>
    Task<IReadOnlyList<FileAsset>> ListReferencedAsync(
        string conversationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 幂等地将会话与文件关联写入 <c>conversation_file_references</c>，已存在的引用保留原时间戳。
    /// </summary>
    Task EnsureConversationReferencesAsync(
        string conversationId,
        IReadOnlyList<string> fileIds,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// 判断会话是否已引用该文件（用于读取时的会话范围校验）。
    /// </summary>
    Task<bool> IsReferencedAsync(
        string conversationId,
        string fileId,
        CancellationToken cancellationToken);
}
