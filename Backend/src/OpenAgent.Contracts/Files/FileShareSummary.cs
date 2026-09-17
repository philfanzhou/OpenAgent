namespace OpenAgent.Contracts.Files;

/// <summary>
/// 分享链接的查询视图。明文令牌只在创建响应中出现，这里不返回 Url，
/// 仅提供用于撤销的 <see cref="ShareId"/> 与运行状态。
/// </summary>
public sealed class FileShareSummary
{
    public required string ShareId { get; init; }
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public required FileShareMode Mode { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public int? MaxDownloads { get; init; }
    public int DownloadCount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>未过期且未达到下载次数上限时为 true；撤销后的记录不再出现于查询结果。</summary>
    public bool IsActive => ExpiresAt > DateTimeOffset.UtcNow
        && (MaxDownloads == null || DownloadCount < MaxDownloads);
}
