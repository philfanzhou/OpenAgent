namespace OpenAgent.Contracts.Files;

public sealed class FileAssetOptions
{
    public const string SectionName = "FileAssets";

    public bool Enabled { get; init; }
    public long MaxFileSizeBytes { get; init; } = 100 * 1024 * 1024;
    public long MaxFunctionReadBytes { get; init; } = 1024 * 1024;
    public long MaxInlineImageBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxInlineImageCount { get; init; } = 4;
    /// <summary>内联图片注入模型请求前的长边上限（像素）；超出则等比降采样。
    /// 0 表示禁用压缩。原始文件不受影响，仅影响发送给模型的字节。</summary>
    public int InlineImageMaxLongEdge { get; init; } = 1568;
    /// <summary>降采样后 JPEG/WebP 的编码质量（1-100）。</summary>
    public int InlineImageQuality { get; init; } = 85;
    /// <summary>历史重放时仍内联图片的最近用户轮次数；更早轮次的图片只保留文件描述符，
    /// 避免每次请求重复携带全部历史图片 token。0 表示历史图片一律不内联。</summary>
    public int InlineImageHistoryTurns { get; init; } = 2;
    public long MaxArchiveInputBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxArchiveFileCount { get; init; } = 100;
    /// <summary>默认白名单由 FileMediaTypeCatalog 派生（目录=事实源，本配置=策略，可收窄不可隐式放宽）。</summary>
    public IReadOnlyList<string> AllowedMediaTypes { get; init; } = FileMediaTypeCatalog.AllMediaTypes;
    public IReadOnlyList<string> AllowedExtensions { get; init; } = FileMediaTypeCatalog.AllExtensions;
}
