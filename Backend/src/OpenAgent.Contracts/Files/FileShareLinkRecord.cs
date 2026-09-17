namespace OpenAgent.Contracts.Files;

/// <summary>
/// 分享链接的持久化记录。数据库只保存令牌的 SHA-256 哈希（<see cref="ShareIdHash"/>），
/// 不保存明文令牌；下载时凭哈希定位记录并原子核销。
/// </summary>
public sealed class FileShareLinkRecord
{
    public required string ShareIdHash { get; init; }
    public required string FileId { get; init; }
    public required string TenantId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string ObjectKey { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public FileShareMode Mode { get; init; }
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>最大下载次数；null 表示有效期内不限次数。</summary>
    public int? MaxDownloads { get; init; }
    public int DownloadCount { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
}
