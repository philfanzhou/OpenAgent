namespace OpenAgent.Contracts.Files;

/// <summary>分享链接核销成功后待返回给下载方的文件内容。</summary>
public sealed class FileShareRedemption
{
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public required byte[] Data { get; init; }
}
