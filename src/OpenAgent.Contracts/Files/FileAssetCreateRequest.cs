namespace OpenAgent.Contracts.Files;

public sealed class FileAssetCreateRequest
{
    public required string FileName { get; init; }
    /// <summary>调用方已知的媒体类型；为空时由服务端按文件扩展名推断规范化类型。</summary>
    public string? MediaType { get; init; }
    public required FileAssetSource Source { get; init; }
}
