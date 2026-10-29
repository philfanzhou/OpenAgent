using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using SkiaSharp;

namespace OpenAgent.Capabilities.Files.Multimodal;

/// <summary>
/// 内联图片注入模型请求前的降采样器。视觉 token 按分辨率计费，把超长边的
/// 图片等比缩到阈值内可量级降低 token 与请求体积；原始文件存储不受影响。
/// 压缩结果按内容摘要（上传时已算好的 Sha256）做有界 LRU 缓存，同一图片
/// 重复上传或历史图片每轮重放时不会重复解码/编码。
/// 处理前先嗅探真实格式并按像素数与内存预算预检（Skia 解码只取首帧，多帧
/// 图的解码内存与单帧图相同）；内容无法识别、与声明的媒体类型不符或超出
/// 预算时抛 <see cref="InlineImageRejectedException"/>，由调用方跳过内联并
/// 记录原因，不得回退为继续内联原始字节。
/// </summary>
internal class InlineImageOptimizer
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

    public virtual byte[] Optimize(FileAsset asset, byte[] data)
    {
        int maxLongEdge = _options.InlineImageMaxLongEdge;
        if (maxLongEdge <= 0 || data.Length == 0 || !IsSupportedRaster(asset.MediaType))
        {
            return data;
        }

        string cacheKey = string.IsNullOrEmpty(asset.Sha256) ? asset.FileId : asset.Sha256;
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out LinkedListNode<CacheEntry>? node))
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
            if (_cache.TryGetValue(cacheKey, out LinkedListNode<CacheEntry>? existing))
            {
                // 并发首访时保留先入缓存的副本，丢弃重复计算的结果。
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Data;
            }

            var entry = new LinkedListNode<CacheEntry>(new CacheEntry(cacheKey, optimized));
            _lru.AddFirst(entry);
            _cache[cacheKey] = entry;
            _cacheBytes += optimized.LongLength;
            while (_cacheBytes > CacheMaxBytes && _lru.Count > 1)
            {
                LinkedListNode<CacheEntry> last = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
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
        VerifyContent(asset, data, out int width, out int height, out SKEncodedOrigin origin);

        // 尺寸在阈值内则无需解码：头信息校验已通过，按原字节内联（动画保持原样，
        // 原始 EXIF 方向仍由消费方自行处理）。
        if (Math.Max(width, height) <= maxLongEdge)
        {
            return data;
        }

        // SkiaSharp 解码只取首帧：多帧图（动画 WebP/APNG）的解码内存与单帧图
        // 相同，帧数炸弹天然受限；需降采样的动画图输出为静态首帧（视觉模型只
        // 消费首帧）。重编码会丢弃 EXIF，方向必须在此处转正，否则竖拍图会横过来。
        using SKData sourceData = SKData.CreateCopy(data);
        using SKBitmap? source = DecodeOriented(sourceData, origin);
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

    private static SKBitmap? DecodeOriented(SKData data, SKEncodedOrigin origin)
    {
        SKBitmap? decoded = SKBitmap.Decode(data);
        if (decoded is null || origin == SKEncodedOrigin.TopLeft)
        {
            return decoded;
        }

        SKBitmap? oriented = OrientDecoded(decoded, origin);
        decoded.Dispose();
        return oriented;
    }

    internal static SKBitmap? OrientDecoded(SKBitmap decoded, SKEncodedOrigin origin)
    {
        bool swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var info = new SKImageInfo(
            swaps ? decoded.Height : decoded.Width,
            swaps ? decoded.Width : decoded.Height,
            decoded.ColorType,
            decoded.AlphaType);
        using SKSurface? surface = SKSurface.Create(info);
        if (surface is null)
        {
            return null;
        }

        SKCanvas canvas = surface.Canvas;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(decoded.Width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(decoded.Width, decoded.Height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, decoded.Height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                // 转置：(x,y) → (y,x)
                canvas.SetMatrix(new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1));
                break;
            case SKEncodedOrigin.RightTop:
                // 顺时针 90°：(x,y) → (H−y, x)
                canvas.Translate(decoded.Height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                // 反转置：(x,y) → (H−y, W−x)
                canvas.SetMatrix(new SKMatrix(0, -1, decoded.Height, -1, 0, decoded.Width, 0, 0, 1));
                break;
            default: // SKEncodedOrigin.LeftBottom
                // 逆时针 90°：(x,y) → (y, W−x)
                canvas.Translate(0, decoded.Width);
                canvas.RotateDegrees(-90);
                break;
        }

        canvas.DrawBitmap(decoded, 0, 0);
        var oriented = new SKBitmap(info);
        using SKImage image = surface.Snapshot();
        return image.ReadPixels(info, oriented.GetPixels(), info.RowBytes, 0, 0) ? oriented : null;
    }

    /// <summary>只解析头信息（尺寸/格式，不解码像素）并完成全部前置校验，通过后输出真实尺寸与 EXIF 方向。</summary>
    private void VerifyContent(FileAsset asset, byte[] data, out int width, out int height, out SKEncodedOrigin origin)
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
        origin = codec.EncodedOrigin;
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

    private sealed record CacheEntry(string Key, byte[] Data);
}
