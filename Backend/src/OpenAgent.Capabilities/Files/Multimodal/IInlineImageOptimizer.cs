using OpenAgent.Contracts.Files;

namespace OpenAgent.Capabilities.Files.Multimodal;

/// <summary>
/// 内联图片注入模型请求前的降采样器。视觉 token 按分辨率计费，把超长边的
/// 图片等比缩到阈值内可量级降低 token 与请求体积；原始文件存储不受影响。
/// 资产内容按 fileId 不可变（上传即新建 id，无内容更新路径），压缩结果用
/// 有界 LRU 缓存，避免历史图片每轮重放时重复解码/编码。
/// 处理前先嗅探真实格式并按像素数与内存预算预检（Skia 解码只取首帧，多帧
/// 图的解码内存与单帧图相同）；内容无法识别、与声明的媒体类型不符或超出
/// 预算时抛 <see cref="InlineImageRejectedException"/>，由调用方跳过内联并
/// 记录原因，不得回退为继续内联原始字节。
/// </summary>
internal interface IInlineImageOptimizer
{
    byte[] Optimize(FileAsset asset, byte[] data);
}
