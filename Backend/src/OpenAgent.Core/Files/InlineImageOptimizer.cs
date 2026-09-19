using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace OpenAgent.Core.Files;

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

internal sealed class InlineImageOptimizer : IInlineImageOptimizer
{
    private const long CacheMaxBytes = 32 * 1024 * 1024;

    private readonly FileAssetOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _cacheBytes;

    public InlineImageOptimizer(IOptions<FileAssetOptions> options)
    {
        _options = options.Value;
    }

    public byte[] Optimize(FileAsset asset, byte[] data)
    {
        int maxLongEdge = _options.InlineImageMaxLongEdge;
        if (maxLongEdge <= 0 || data.Length == 0 || !IsSupportedRaster(asset.MediaType))
        {
            return data;
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(asset.FileId, out LinkedListNode<CacheEntry>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Data;
            }
        }

        byte[] optimized = Downscale(data, asset.MediaType, maxLongEdge);

        lock (_lock)
        {
            if (_cache.TryGetValue(asset.FileId, out LinkedListNode<CacheEntry>? existing))
            {
                // 并发首访时保留先入缓存的副本，丢弃重复计算的结果。
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Data;
            }

            var entry = new LinkedListNode<CacheEntry>(new CacheEntry(asset.FileId, optimized));
            _lru.AddFirst(entry);
            _cache[asset.FileId] = entry;
            _cacheBytes += optimized.LongLength;
            while (_cacheBytes > CacheMaxBytes && _lru.Count > 1)
            {
                LinkedListNode<CacheEntry> last = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(last.Value.FileId);
                _cacheBytes -= last.Value.Data.LongLength;
            }
        }

        return optimized;
    }

    private byte[] Downscale(byte[] data, string mediaType, int maxLongEdge)
    {
        try
        {
            using Image image = Image.Load(data);
            if (Math.Max(image.Width, image.Height) <= maxLongEdge)
            {
                return data;
            }

            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxLongEdge, maxLongEdge)
            }));

            using MemoryStream output = new();
            switch (NormalizeMediaType(mediaType))
            {
                case "image/jpeg":
                    image.Save(output, new JpegEncoder { Quality = _options.InlineImageQuality });
                    break;
                case "image/webp":
                    image.Save(output, new WebpEncoder { Quality = _options.InlineImageQuality });
                    break;
                default:
                    image.Save(output, new PngEncoder());
                    break;
            }

            byte[] encoded = output.ToArray();
            // 已高度优化的源图重编码可能更大，此时保留原字节。
            return encoded.LongLength < data.LongLength ? encoded : data;
        }
        catch
        {
            // 无法解码的图片按原字节内联，不阻断执行。
            return data;
        }
    }

    private static bool IsSupportedRaster(string mediaType) => NormalizeMediaType(mediaType) switch
    {
        "image/png" or "image/jpeg" or "image/webp" => true,
        _ => false
    };

    private static string NormalizeMediaType(string mediaType) =>
        mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();

    private sealed record CacheEntry(string FileId, byte[] Data);
}
