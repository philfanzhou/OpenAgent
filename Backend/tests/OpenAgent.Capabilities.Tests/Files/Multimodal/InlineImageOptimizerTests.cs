using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Capabilities.Files.Multimodal;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Files;

public sealed class InlineImageOptimizerTests
{
    [Fact]
    public void Optimize_LargePng_DownscaleToConfiguredLongEdge()
    {
        byte[] source = CreatePng(3200, 1600);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);
        FileAsset asset = CreateAsset("image/png");

        byte[] result = optimizer.Optimize(asset, source);

        using Image image = Image.Load(result);
        Assert.Equal(1568, image.Width);
        Assert.Equal(784, image.Height);
    }

    [Fact]
    public void Optimize_JpegMediaType_DownscaleKeepsMediaType()
    {
        byte[] source = CreateJpeg(2000, 1000);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1024);
        FileAsset asset = CreateAsset("image/jpeg");

        byte[] result = optimizer.Optimize(asset, source);

        using Image image = Image.Load(result);
        Assert.Equal(1024, image.Width);
        Assert.Equal(512, image.Height);
    }

    [Fact]
    public void Optimize_SmallImage_ReturnsOriginalBytes()
    {
        byte[] source = CreatePng(800, 600);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);

        Assert.Same(source, optimizer.Optimize(CreateAsset("image/png"), source));
    }

    [Fact]
    public void Optimize_UnsupportedMediaType_ReturnsOriginalBytes()
    {
        byte[] source = [0x3c, 0x73, 0x76, 0x67];
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);

        Assert.Same(source, optimizer.Optimize(CreateAsset("image/svg+xml"), source));
    }

    [Fact]
    public void Optimize_DisabledByConfig_ReturnsOriginalBytes()
    {
        byte[] source = CreatePng(3200, 1600);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 0);

        Assert.Same(source, optimizer.Optimize(CreateAsset("image/png"), source));
    }

    [Fact]
    public void Optimize_SecondCall_ServedFromCache()
    {
        byte[] source = CreatePng(3200, 1600);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);
        FileAsset asset = CreateAsset("image/png");

        byte[] first = optimizer.Optimize(asset, source);
        byte[] second = optimizer.Optimize(asset, source);

        Assert.Same(first, second);
        Assert.NotSame(source, first);
    }

    [Fact]
    public void Optimize_UnrecognizableContent_ThrowsClearRejection()
    {
        byte[] source = [0x00, 0x01, 0x02, 0x03];
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);
        FileAsset asset = CreateAsset("image/png");

        InlineImageRejectedException ex = Assert.Throws<InlineImageRejectedException>(
            () => optimizer.Optimize(asset, source));

        // 无论嗅探判为无法识别还是头解析失败，都必须给出带 fileId 的明确拒绝。
        Assert.Contains(asset.FileId, ex.Message);
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public void Optimize_MalformedPngHeader_ThrowsClearRejection()
    {
        // 合法 PNG 签名 + 后续损坏数据：嗅探可识别格式，但头信息解析失败。
        byte[] source = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);
        FileAsset asset = CreateAsset("image/png");

        InlineImageRejectedException ex = Assert.Throws<InlineImageRejectedException>(
            () => optimizer.Optimize(asset, source));

        Assert.Contains(asset.FileId, ex.Message);
    }

    [Fact]
    public void Optimize_ActualFormatDiffersFromDeclared_ThrowsClearRejection()
    {
        byte[] jpegBytes = CreateJpeg(2000, 1000);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1024);
        FileAsset asset = CreateAsset("image/png");

        InlineImageRejectedException ex = Assert.Throws<InlineImageRejectedException>(
            () => optimizer.Optimize(asset, jpegBytes));

        Assert.Contains("declared image/png", ex.Message);
        Assert.Contains("image/jpeg", ex.Message);
    }

    [Fact]
    public void Optimize_GifDeclaredAsPng_ThrowsClearRejection()
    {
        byte[] gifHeader = "GIF89a"u8.ToArray();
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);
        FileAsset asset = CreateAsset("image/png");

        InlineImageRejectedException ex = Assert.Throws<InlineImageRejectedException>(
            () => optimizer.Optimize(asset, gifHeader));

        Assert.Contains("image/gif", ex.Message);
    }

    [Fact]
    public void Optimize_PixelCountOverLimit_ThrowsClearRejection()
    {
        byte[] source = CreatePng(800, 600);
        InlineImageOptimizer optimizer = CreateOptimizer(
            maxLongEdge: 1568, maxPixels: 100_000);
        FileAsset asset = CreateAsset("image/png");

        InlineImageRejectedException ex = Assert.Throws<InlineImageRejectedException>(
            () => optimizer.Optimize(asset, source));

        Assert.Contains("800x600", ex.Message);
        Assert.Contains("480000 pixels", ex.Message);
        Assert.Contains("100000", ex.Message);
    }

    [Fact]
    public void Optimize_DecodeBudgetExceeded_ThrowsClearRejection()
    {
        // 800×600×4B/px = 1920000 解码字节，预算 1000000 不足以容纳单帧。
        byte[] source = CreatePng(800, 600);
        InlineImageOptimizer optimizer = CreateOptimizer(
            maxLongEdge: 1568, maxDecodedBytes: 1_000_000);
        FileAsset asset = CreateAsset("image/png");

        InlineImageRejectedException ex = Assert.Throws<InlineImageRejectedException>(
            () => optimizer.Optimize(asset, source));

        Assert.Contains("1920000", ex.Message);
        Assert.Contains("1000000", ex.Message);
    }

    [Fact]
    public void Optimize_WithinLimitsButNeedsResize_DownscaleSucceeds()
    {
        // 预算恰好容纳单帧大图：不因预算收紧而拒绝，正常降采样。
        byte[] source = CreatePng(2000, 1000);
        InlineImageOptimizer optimizer = CreateOptimizer(
            maxLongEdge: 1024, maxFrames: 1, maxDecodedBytes: 2000L * 1000 * 4);
        FileAsset asset = CreateAsset("image/png");

        byte[] result = optimizer.Optimize(asset, source);

        using Image image = Image.Load(result);
        Assert.Equal(1024, image.Width);
    }

    [Fact]
    public void ComputeMaxDecodeFrames_BudgetTightensConfiguredFrameCap()
    {
        FileAssetOptions options = new()
        {
            Enabled = true,
            InlineImageMaxFrames = 8,
            InlineImageMaxDecodedBytes = 200_000
        };

        // 10000 像素 × 4B = 40000 字节/帧；预算 200000 → 5 帧 < 配置上限 8。
        int frames = InlineImageOptimizer.ComputeMaxDecodeFrames(10_000, options, out long frameBytes);

        Assert.Equal(5, frames);
        Assert.Equal(40_000, frameBytes);
    }

    [Fact]
    public void ComputeMaxDecodeFrames_BudgetBelowSingleFrame_ReturnsZero()
    {
        FileAssetOptions options = new()
        {
            Enabled = true,
            InlineImageMaxFrames = 8,
            InlineImageMaxDecodedBytes = 39_999
        };

        Assert.Equal(0, InlineImageOptimizer.ComputeMaxDecodeFrames(10_000, options, out _));
    }

    [Fact]
    public void ComputeMaxDecodeFrames_LargeBudget_KeepsConfiguredCap()
    {
        FileAssetOptions options = new()
        {
            Enabled = true,
            InlineImageMaxFrames = 8,
            InlineImageMaxDecodedBytes = long.MaxValue
        };

        Assert.Equal(8, InlineImageOptimizer.ComputeMaxDecodeFrames(10_000, options, out _));
    }

    private static InlineImageOptimizer CreateOptimizer(
        int maxLongEdge,
        long? maxPixels = null,
        int? maxFrames = null,
        long? maxDecodedBytes = null)
    {
        FileAssetOptions defaults = new();
        return new InlineImageOptimizer(Options.Create(new FileAssetOptions
        {
            Enabled = true,
            InlineImageMaxLongEdge = maxLongEdge,
            InlineImageQuality = 85,
            InlineImageMaxPixels = maxPixels ?? defaults.InlineImageMaxPixels,
            InlineImageMaxFrames = maxFrames ?? defaults.InlineImageMaxFrames,
            InlineImageMaxDecodedBytes = maxDecodedBytes ?? defaults.InlineImageMaxDecodedBytes
        }));
    }

    private static FileAsset CreateAsset(string mediaType) => new()
    {
        FileId = Guid.NewGuid().ToString("N"),
        TenantId = "tenant-1",
        OwnerUserId = "user-1",
        FileName = "image",
        MediaType = mediaType,
        Length = 1,
        Sha256 = "sha",
        ObjectKey = "files/tenant-1/image",
        Source = FileAssetSource.UserUpload,
        State = FileAssetState.Ready,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static byte[] CreatePng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using MemoryStream output = new();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using MemoryStream output = new();
        image.SaveAsJpeg(output);
        return output.ToArray();
    }
}
