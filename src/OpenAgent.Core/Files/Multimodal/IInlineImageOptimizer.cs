using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp;

namespace OpenAgent.Core.Files.Multimodal;

/// <summary>
/// 内联图片注入模型请求前的降采样器。视觉 token 按分辨率计费，把超长边的
/// 图片等比缩到阈值内可量级降低 token 与请求体积；原始文件存储不受影响。
/// 资产内容按 fileId 不可变（上传即新建 id，无内容更新路径），压缩结果用
/// 有界 LRU 缓存，避免历史图片每轮重放时重复解码/编码。
/// </summary>
internal interface IInlineImageOptimizer
{
    byte[] Optimize(FileAsset asset, byte[] data);
}
