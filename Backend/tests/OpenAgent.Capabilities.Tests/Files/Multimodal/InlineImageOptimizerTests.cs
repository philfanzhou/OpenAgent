using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Capabilities.Files.Multimodal;
using SkiaSharp;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Files;

public sealed class InlineImageOptimizerTests
{
    [Fact]
    public void Optimize_LargePng_DownscaleToConfiguredLongEdge()
    {
        byte[] source = CreateImage(SKEncodedImageFormat.Png, 3200, 1600);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);
        FileAsset asset = CreateAsset("image/png");

        byte[] result = optimizer.Optimize(asset, source);

        using SKBitmap decoded = SKBitmap.Decode(result);
        Assert.Equal(1568, decoded.Width);
        Assert.Equal(784, decoded.Height);
    }

    [Fact]
    public void Optimize_JpegMediaType_DownscaleKeepsMediaType()
    {
        byte[] source = CreateImage(SKEncodedImageFormat.Jpeg, 2000, 1000);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1024);
        FileAsset asset = CreateAsset("image/jpeg");

        byte[] result = optimizer.Optimize(asset, source);

        using SKBitmap decoded = SKBitmap.Decode(result);
        Assert.Equal(1024, decoded.Width);
        Assert.Equal(512, decoded.Height);
    }

    [Fact]
    public void Optimize_LargeWebp_DownscaleToConfiguredLongEdge()
    {
        byte[] source = CreateImage(SKEncodedImageFormat.Webp, 2000, 1000);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1024);
        FileAsset asset = CreateAsset("image/webp");

        byte[] result = optimizer.Optimize(asset, source);

        using SKBitmap decoded = SKBitmap.Decode(result);
        Assert.Equal(1024, decoded.Width);
        Assert.Equal(512, decoded.Height);
    }

    [Fact]
    public void Optimize_SmallImage_ReturnsOriginalBytes()
    {
        byte[] source = CreateImage(SKEncodedImageFormat.Png, 800, 600);
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
        byte[] source = CreateImage(SKEncodedImageFormat.Png, 3200, 1600);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 0);

        Assert.Same(source, optimizer.Optimize(CreateAsset("image/png"), source));
    }

    [Fact]
    public void Optimize_SecondCall_ServedFromCache()
    {
        byte[] source = CreateImage(SKEncodedImageFormat.Png, 3200, 1600);
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

        Assert.Contains(asset.FileId, ex.Message);
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public void Optimize_MalformedPngHeader_ThrowsClearRejection()
    {
        // 合法 PNG 签名 + 后续损坏数据：无法解析出有效头信息。
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
        byte[] jpegBytes = CreateImage(SKEncodedImageFormat.Jpeg, 2000, 1000);
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
        // 最小合法 GIF（GIF89a + 1x1 逻辑屏幕描述符 + 结束符）。
        byte[] source =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, // GIF89a
            0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, // 1x1, flags
            0x3B // trailer
        ];
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);
        FileAsset asset = CreateAsset("image/png");

        InlineImageRejectedException ex = Assert.Throws<InlineImageRejectedException>(
            () => optimizer.Optimize(asset, source));

        Assert.Contains(asset.FileId, ex.Message);
    }

    [Fact]
    public void Optimize_PixelCountOverLimit_ThrowsClearRejection()
    {
        byte[] source = CreateImage(SKEncodedImageFormat.Png, 800, 600);
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
        // 800×600×4B/px = 1920000 解码字节，预算 1000000 不足。
        byte[] source = CreateImage(SKEncodedImageFormat.Png, 800, 600);
        InlineImageOptimizer optimizer = CreateOptimizer(
            maxLongEdge: 1568, maxDecodedBytes: 1_000_000);
        FileAsset asset = CreateAsset("image/png");

        InlineImageRejectedException ex = Assert.Throws<InlineImageRejectedException>(
            () => optimizer.Optimize(asset, source));

        Assert.Contains("1920000", ex.Message);
        Assert.Contains("1000000", ex.Message);
    }

    [Fact]
    public void Optimize_WithinBudgetLargeImage_DownscaleSucceeds()
    {
        // 预算恰好容纳单帧大图：不因预算收紧而拒绝，正常降采样。
        byte[] source = CreateImage(SKEncodedImageFormat.Png, 2000, 1000);
        InlineImageOptimizer optimizer = CreateOptimizer(
            maxLongEdge: 1024, maxDecodedBytes: 2000L * 1000 * 4);
        FileAsset asset = CreateAsset("image/png");

        byte[] result = optimizer.Optimize(asset, source);

        using SKBitmap decoded = SKBitmap.Decode(result);
        Assert.Equal(1024, decoded.Width);
    }

    private static InlineImageOptimizer CreateOptimizer(
        int maxLongEdge,
        long? maxPixels = null,
        long? maxDecodedBytes = null)
    {
        FileAssetOptions defaults = new();
        return new InlineImageOptimizer(Options.Create(new FileAssetOptions
        {
            Enabled = true,
            InlineImageMaxLongEdge = maxLongEdge,
            InlineImageQuality = 85,
            InlineImageMaxPixels = maxPixels ?? defaults.InlineImageMaxPixels,
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

    private static byte[] CreateImage(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }
}
