namespace OpenAgent.Infrastructure.Entities;

internal sealed class FileShareLinkEntity
{
    public required string ShareIdHash { get; init; }
    public required string FileId { get; init; }
    public required string TenantId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string ObjectKey { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public int Mode { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public int? MaxDownloads { get; init; }
    public int DownloadCount { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
}
