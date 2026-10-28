using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using SkiaSharp;

namespace OpenAgent.Capabilities.Files.Multimodal;

internal sealed class InlineImageOptimizer : IInlineImageOptimizer
{
    private const long CacheMaxBytes = 32 * 1024 * 1024;
    // 头信息不含像素格式，按 32bpp 保守估算解码内存（高估即更安全）。
    private const int EstimatedBytesPerPixel = 4;
    // Skia 推荐的缩小重采样滤镜（Mitchell B=C=1/3）。
    private static readonly SKSamplingOptions DownscaleSampling =
        new(new SKCubicResampler(1f / 3f, 1f / 3f));

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

        // 拒绝（格式不符/畸形/超限）以异常上抛：由调用方记录并跳过内联，
        // 不缓存失败结果，也不回退为把原始字节继续发给模型。
        byte[] optimized = ProcessInlineImage(asset, data, maxLongEdge);

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

    /// <summary>
    /// 内联前的安全处理管线：先按内容嗅探真实格式并读取头信息（不解码像素），
    /// 校验声明格式一致、单帧像素数与解码内存预算后才解码降采样。任一环节
    /// 不通过抛 <see cref="InlineImageRejectedException"/>——小体积头部可以声明
    /// 超大尺寸，解码前的预算检查是防解压炸弹的关键；异常绝不能被吞掉后拿
    /// 原始字节继续处理。
    /// </summary>
    private byte[] ProcessInlineImage(FileAsset asset, byte[] data, int maxLongEdge)
    {
        VerifyContent(asset, data, out int width, out int height);

        // 尺寸在阈值内则无需解码：头信息校验已通过，按原字节内联（动画保持原样）。
        if (Math.Max(width, height) <= maxLongEdge)
        {
            return data;
        }

        // SkiaSharp 解码只取首帧：多帧图（动画 WebP/APNG）的解码内存与单帧图
        // 相同，帧数炸弹天然受限；需降采样的动画图输出为静态首帧（视觉模型只
        // 消费首帧）。
        using SKData sourceData = SKData.CreateCopy(data);
        using SKBitmap? source = SKBitmap.Decode(sourceData);
        if (source is null || source.Width <= 0 || source.Height <= 0)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{asset.FileId}' rejected: image data could not be decoded.");
        }

        double scale = Math.Min(
            (double)maxLongEdge / source.Width,
            (double)maxLongEdge / source.Height);
        var targetInfo = new SKImageInfo(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            source.ColorType,
            source.AlphaType);
        using var resized = new SKBitmap(targetInfo);
        if (!source.ScalePixels(resized, DownscaleSampling))
        {
            throw new InlineImageRejectedException(
                $"Inline image '{asset.FileId}' rejected: image resize failed.");
        }

        byte[] encoded = Encode(asset, resized, NormalizeMediaType(asset.MediaType));
        // 已高度优化的源图重编码可能更大，此时保留原字节。
        return encoded.LongLength < data.LongLength ? encoded : data;
    }

    /// <summary>只解析头信息（尺寸/格式，不解码像素）并完成全部前置校验，通过后输出真实尺寸。</summary>
    private void VerifyContent(FileAsset asset, byte[] data, out int width, out int height)
    {
        string fileId = asset.FileId;
        string declared = NormalizeMediaType(asset.MediaType);
        width = 0;
        height = 0;

        // 上传不对媒体类型做内容校验（声明可任意指定），内联只信任嗅探到的真实格式。
        using SKData sourceData = SKData.CreateCopy(data);
        using SKCodec? codec = SKCodec.Create(sourceData);
        if (codec is null)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{fileId}' rejected: content is not a recognizable image (declared {declared}).");
        }

        string actual = MapMediaType(codec.EncodedFormat);
        if (!string.Equals(actual, declared, StringComparison.OrdinalIgnoreCase))
        {
            throw new InlineImageRejectedException(
                $"Inline image '{fileId}' rejected: declared {declared} but actual content is {actual}; only content-verified PNG/JPEG/WebP images are inlined.");
        }

        width = codec.Info.Width;
        height = codec.Info.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{fileId}' rejected: malformed image header (missing dimensions).");
        }

        long pixels = (long)width * height;
        if (pixels > _options.InlineImageMaxPixels)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{fileId}' rejected: {width}x{height} = {pixels} pixels exceeds the per-frame limit {_options.InlineImageMaxPixels} (FileAssets:InlineImageMaxPixels).");
        }

        long decodedBytes = pixels * EstimatedBytesPerPixel;
        if (decodedBytes > _options.InlineImageMaxDecodedBytes)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{fileId}' rejected: {width}x{height} ≈ {decodedBytes} decoded bytes exceeds the decode budget {_options.InlineImageMaxDecodedBytes} (FileAssets:InlineImageMaxDecodedBytes).");
        }
    }

    private byte[] Encode(FileAsset asset, SKBitmap bitmap, string mediaType)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using SKData? encoded = mediaType switch
        {
            "image/jpeg" => image.Encode(SKEncodedImageFormat.Jpeg, _options.InlineImageQuality),
            "image/webp" => image.Encode(SKEncodedImageFormat.Webp, _options.InlineImageQuality),
            _ => image.Encode(SKEncodedImageFormat.Png, 100)
        };
        if (encoded is null || encoded.Size == 0)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{asset.FileId}' rejected: image encoding failed.");
        }

        return encoded.ToArray();
    }

    private static string MapMediaType(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => "image/png",
        SKEncodedImageFormat.Jpeg => "image/jpeg",
        SKEncodedImageFormat.Webp => "image/webp",
        _ => $"image/{format.ToString().ToLowerInvariant()}"
    };

    private static bool IsSupportedRaster(string mediaType) => NormalizeMediaType(mediaType) switch
    {
        "image/png" or "image/jpeg" or "image/webp" => true,
        _ => false
    };

    private static string NormalizeMediaType(string mediaType) =>
        mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();

    private sealed record CacheEntry(string FileId, byte[] Data);
}
