namespace OpenAgent.Contracts.Files;

/// <summary>创建分享链接后返回给所有者的结果。Url 是唯一的下载凭证，不暴露对象存储地址。</summary>
public sealed class FileShareLink
{
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public required FileShareMode Mode { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public int? MaxDownloads { get; init; }
    public int DownloadCount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
