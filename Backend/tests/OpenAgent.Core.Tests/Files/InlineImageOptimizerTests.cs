using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace OpenAgent.Core.Tests.Files;

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
    public void Optimize_UndecodableImage_ReturnsOriginalBytes()
    {
        byte[] source = [0x89, 0x50];
        InlineImageOptimizer optimizer = CreateOptimizer(maxLongEdge: 1568);

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

    private static InlineImageOptimizer CreateOptimizer(int maxLongEdge) => new(Options.Create(
        new FileAssetOptions
        {
            Enabled = true,
            InlineImageMaxLongEdge = maxLongEdge,
            InlineImageQuality = 85
        }));

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
