using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace OpenAgent.Capabilities.Files.Multimodal;

internal sealed class InlineImageOptimizer : IInlineImageOptimizer
{
    private const long CacheMaxBytes = 32 * 1024 * 1024;
    // 识别阶段不保证给出像素位深，按 32bpp 保守估算解码内存（高估即更安全）。
    private const int EstimatedBytesPerPixel = 4;

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
    /// 校验声明格式一致、单帧像素数与解码内存预算（预算同时约束帧数）后才解码降采样。
    /// 任一环节不通过抛 <see cref="InlineImageRejectedException"/>——小体积头部
    /// 可以声明超大尺寸，解码前的预算检查是防解压炸弹的关键；异常绝不能被
    /// 吞掉后拿原始字节继续处理。
    /// </summary>
    private byte[] ProcessInlineImage(FileAsset asset, byte[] data, int maxLongEdge)
    {
        VerifyContent(asset, data, out int width, out int height, out long frameBytes, out int maxDecodeFrames);

        // 尺寸在阈值内则无需解码：头信息校验已通过，按原字节内联。
        if (Math.Max(width, height) <= maxLongEdge)
        {
            return data;
        }

        var decodeOptions = new DecoderOptions { MaxFrames = (uint)maxDecodeFrames };
        Image image;
        try
        {
            image = Image.Load(decodeOptions, data);
        }
        catch (ImageFormatException ex)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{asset.FileId}' rejected: image data could not be decoded ({ex.Message}).");
        }

        using (image)
        {
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxLongEdge, maxLongEdge)
            }));

            using MemoryStream output = new();
            switch (NormalizeMediaType(asset.MediaType))
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
    }

    /// <summary>读取头信息（不解码像素）并完成全部前置校验；通过后输出真实尺寸、单帧估算字节与允许解码的帧数。</summary>
    private void VerifyContent(
        FileAsset asset,
        byte[] data,
        out int width,
        out int height,
        out long frameBytes,
        out int maxDecodeFrames)
    {
        string fileId = asset.FileId;
        string declared = NormalizeMediaType(asset.MediaType);
        width = 0;
        height = 0;

        // 上传不对媒体类型做内容校验（声明可任意指定），内联只信任嗅探到的真实格式。
        try
        {
            IImageFormat? format = Image.DetectFormat(data);
            if (format is null)
            {
                throw new InlineImageRejectedException(
                    $"Inline image '{fileId}' rejected: content is not a recognizable image (declared {declared}).");
            }

            if (!string.Equals(format.DefaultMimeType, declared, StringComparison.OrdinalIgnoreCase))
            {
                throw new InlineImageRejectedException(
                    $"Inline image '{fileId}' rejected: declared {declared} but actual content is {format.DefaultMimeType}; only content-verified PNG/JPEG/WebP images are inlined.");
            }

            ImageInfo? info = Image.Identify(data);
            if (info is null || info.Width <= 0 || info.Height <= 0)
            {
                throw new InlineImageRejectedException(
                    $"Inline image '{fileId}' rejected: malformed image header (missing dimensions).");
            }

            width = info.Width;
            height = info.Height;
        }
        catch (ImageFormatException ex)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{fileId}' rejected: malformed image data ({ex.Message}).");
        }

        long pixels = (long)width * height;
        if (pixels > _options.InlineImageMaxPixels)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{fileId}' rejected: {width}x{height} = {pixels} pixels exceeds the per-frame limit {_options.InlineImageMaxPixels} (FileAssets:InlineImageMaxPixels).");
        }

        maxDecodeFrames = ComputeMaxDecodeFrames(pixels, _options, out frameBytes);
        if (maxDecodeFrames < 1)
        {
            throw new InlineImageRejectedException(
                $"Inline image '{fileId}' rejected: {width}x{height} ≈ {frameBytes} decoded bytes per frame exceeds the decode budget {_options.InlineImageMaxDecodedBytes} (FileAssets:InlineImageMaxDecodedBytes).");
        }
    }

    /// <summary>在配置帧数上限内，按解码内存预算进一步收紧允许解码的帧数；返回 0 表示单帧即超预算。</summary>
    internal static int ComputeMaxDecodeFrames(long pixels, FileAssetOptions options, out long frameBytes)
    {
        frameBytes = pixels * EstimatedBytesPerPixel;
        long framesByBudget = options.InlineImageMaxDecodedBytes / Math.Max(frameBytes, 1);
        return (int)Math.Min(options.InlineImageMaxFrames, framesByBudget);
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
