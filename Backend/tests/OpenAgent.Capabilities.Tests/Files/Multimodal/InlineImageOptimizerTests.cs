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

    [Fact]
    public void Optimize_SameContentDifferentFileId_ServedFromSharedCache()
    {
        // 缓存键是内容摘要：同一图片重复上传（新 fileId、同 Sha256）不重复解码。
        byte[] source = CreateImage(SKEncodedImageFormat.Png, 3200, 1600);
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);
        FileAsset assetA = CreateAsset("image/png", fileId: "file-a", sha256: "digest-1");
        FileAsset assetB = CreateAsset("image/png", fileId: "file-b", sha256: "digest-1");

        byte[] first = optimizer.Optimize(assetA, source);
        byte[] second = optimizer.Optimize(assetB, source);

        Assert.Same(first, second);
    }

    // 基准 2x2：TL=红 FF0000、TR=绿 008000、BL=蓝 0000FF、BR=黄 FFFF00。
    [Theory]
    [InlineData(SKEncodedOrigin.RightTop, "0000FF", "FF0000", "FFFF00", "008000")]
    [InlineData(SKEncodedOrigin.LeftBottom, "008000", "FFFF00", "FF0000", "0000FF")]
    [InlineData(SKEncodedOrigin.BottomRight, "FFFF00", "0000FF", "008000", "FF0000")]
    [InlineData(SKEncodedOrigin.TopRight, "008000", "FF0000", "FFFF00", "0000FF")]
    [InlineData(SKEncodedOrigin.BottomLeft, "0000FF", "FFFF00", "FF0000", "008000")]
    [InlineData(SKEncodedOrigin.LeftTop, "FF0000", "0000FF", "008000", "FFFF00")]
    [InlineData(SKEncodedOrigin.RightBottom, "FFFF00", "008000", "0000FF", "FF0000")]
    public void OrientDecoded_AppliesExifTransform(
        SKEncodedOrigin origin,
        string expectedTopLeft,
        string expectedTopRight,
        string expectedBottomLeft,
        string expectedBottomRight)
    {
        using SKBitmap source = CreateQuad(
            SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow);

        using SKBitmap? oriented = InlineImageOptimizer.OrientDecoded(source, origin);

        Assert.NotNull(oriented);
        Assert.Equal(SKColor.Parse(expectedTopLeft), oriented!.GetPixel(0, 0));
        Assert.Equal(SKColor.Parse(expectedTopRight), oriented.GetPixel(1, 0));
        Assert.Equal(SKColor.Parse(expectedBottomLeft), oriented.GetPixel(0, 1));
        Assert.Equal(SKColor.Parse(expectedBottomRight), oriented.GetPixel(1, 1));
    }

    [Fact]
    public void OrientDecoded_QuarterTurnsSwapDimensions()
    {
        using SKBitmap wide = new(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using SKBitmap? rotated = InlineImageOptimizer.OrientDecoded(wide, SKEncodedOrigin.RightTop);
        Assert.NotNull(rotated);
        Assert.Equal(2, rotated!.Width);
        Assert.Equal(4, rotated.Height);
    }

    private static SKBitmap CreateQuad(SKColor topLeft, SKColor topRight, SKColor bottomLeft, SKColor bottomRight)
    {
        var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bitmap.SetPixel(0, 0, topLeft);
        bitmap.SetPixel(1, 0, topRight);
        bitmap.SetPixel(0, 1, bottomLeft);
        bitmap.SetPixel(1, 1, bottomRight);
        return bitmap;
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

    private static FileAsset CreateAsset(string mediaType, string? fileId = null, string? sha256 = null) => new()
    {
        FileId = fileId ?? Guid.NewGuid().ToString("N"),
        TenantId = "tenant-1",
        OwnerUserId = "user-1",
        FileName = "image",
        MediaType = mediaType,
        Length = 1,
        Sha256 = sha256 ?? Guid.NewGuid().ToString("N"),
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
