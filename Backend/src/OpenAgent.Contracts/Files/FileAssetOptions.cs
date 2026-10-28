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
    /// <summary>内联图片单帧最大像素数（宽×高）。解码前按头信息校验，超出则拒绝内联该图，
    /// 模型仍可通过工具读取原文件；默认 2^26 像素（约 8K×8K）。仅作用于内联处理，不影响上传。</summary>
    public long InlineImageMaxPixels { get; init; } = 67_108_864;
    /// <summary>内联图片最多解码的帧数，多帧图截断到该帧数以约束解码内存；仅作用于内联处理。</summary>
    public int InlineImageMaxFrames { get; init; } = 8;
    /// <summary>内联图片解码内存预算（估算 = 帧数×宽×高×每像素字节）。超出则拒绝内联，
    /// 防止小体积头部声明超大尺寸的解压炸弹在解码时耗尽内存。仅作用于内联处理。</summary>
    public long InlineImageMaxDecodedBytes { get; init; } = 256 * 1024 * 1024;
    /// <summary>降采样后 JPEG/WebP 的编码质量（1-100）。</summary>
    public int InlineImageQuality { get; init; } = 85;
    /// <summary>历史重放时仍内联图片的最近用户轮次数；更早轮次的图片只保留文件描述符，
    /// 避免每次请求重复携带全部历史图片 token。0 表示历史图片一律不内联。</summary>
    public int InlineImageHistoryTurns { get; init; } = 2;
    public long MaxArchiveInputBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxArchiveFileCount { get; init; } = 100;
}
