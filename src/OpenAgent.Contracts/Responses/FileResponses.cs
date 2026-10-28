namespace OpenAgent.Contracts.Responses;

/// <summary>
/// 文件资产元数据（POST /api/v1/agent/files、GET /api/v1/agent/files/{fileId}）。
/// 与 <see cref="Files.FileAsset"/> 字段一致，但不包含对象存储内部地址以外的敏感字段。
/// </summary>
public sealed class FileAssetResponse
{
    public required string FileId { get; init; }

    public string? TenantId { get; init; }

    public string? OwnerUserId { get; init; }

    public required string FileName { get; init; }

    public string? MediaType { get; init; }

    public long Length { get; init; }

    public string? Sha256 { get; init; }

    public string? ObjectKey { get; init; }

    public Files.FileAssetSource Source { get; init; }

    public Files.FileAssetState State { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 分享链接响应（创建与列表共用）。创建响应包含 <see cref="Url"/>（唯一下载凭证）；
/// 列表项不含 Url，但含 <see cref="CreatedAt"/>。
/// </summary>
public sealed class FileShareLinkResponse
{
    public required string ShareId { get; init; }

    public required string FileId { get; init; }

    public required string FileName { get; init; }

    public required string MediaType { get; init; }

    public long Length { get; init; }

    /// <summary>分享模式名（temporary / singleUse / longTerm / custom）。</summary>
    public required string Mode { get; init; }

    /// <summary>下载地址（含令牌）；仅创建响应返回，列表项为 null。</summary>
    public string? Url { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>最大下载次数；null 表示有效期内不限次。</summary>
    public int? MaxDownloads { get; init; }

    public int DownloadCount { get; init; }

    /// <summary>创建时间；仅列表项返回，创建响应为 null。</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}
