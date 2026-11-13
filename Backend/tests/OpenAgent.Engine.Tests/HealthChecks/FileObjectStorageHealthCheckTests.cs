using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Files;
using OpenAgent.Engine.Host.Files;
using OpenAgent.Engine.Host.Health;
using OpenAgent.Framework.ObjectStorage;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class FileObjectStorageHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_BucketAccessible_ReturnsHealthy()
    {
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);
        s3.Setup(client => client.HeadBucketAsync(It.IsAny<HeadBucketRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeadBucketResponse { HttpStatusCode = HttpStatusCode.OK });

        HealthCheckResult result = await Check(s3.Object).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(true, result.Data["enabled"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CheckHealthAsync_BucketUnavailable_ReturnsUnhealthyWithoutSensitiveErrors(HttpStatusCode status)
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.HeadBucketAsync(It.IsAny<HeadBucketRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("private storage endpoint") { StatusCode = status });

        HealthCheckResult result = await Check(s3.Object).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Exception);
        Assert.DoesNotContain("private storage endpoint", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_CancellationDuringProbe_DoesNotWaitForUnresponsiveStorage()
    {
        using CancellationTokenSource cancellation = new();
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.HeadBucketAsync(It.IsAny<HeadBucketRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel())
            .Returns(new TaskCompletionSource<HeadBucketResponse>().Task);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Check(s3.Object).CheckHealthAsync(new HealthCheckContext(), cancellation.Token));
    }

    [Fact]
    public async Task CheckHealthAsync_EnabledWithoutClient_ReturnsUnhealthy()
    {
        HealthCheckResult result = await Check().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(true, result.Data["enabled"]);
    }

    [Fact]
    public async Task CheckHealthAsync_DisabledStorageWithoutClient_ReportsDisabled()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileAssets:Enabled"] = "false" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileAssetObjectStorage(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IAmazonS3>());
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        HealthReportEntry storage = report.Entries["file-object-storage"];
        Assert.Equal(HealthStatus.Healthy, storage.Status);
        Assert.Equal(false, storage.Data["enabled"]);
        Assert.Equal("s3", storage.Data["provider"]);
    }

    private static FileObjectStorageHealthCheck Check(IAmazonS3? s3 = null) => new(
        Options.Create(new FileAssetOptions { Enabled = true }),
        Options.Create(new FileObjectStorageOptions { BucketName = "health-test" }), s3);
}
