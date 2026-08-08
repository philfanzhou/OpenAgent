using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Engine.Host.Attachments;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AttachmentStorageHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenBucketIsAvailable_ReturnsHealthy()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.GetBucketLocationAsync(
                It.Is<GetBucketLocationRequest>(request => request.BucketName == "attachments-test"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetBucketLocationResponse());
        var check = CreateCheck(s3.Object);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenS3RejectsRequest_ReturnsUnhealthy()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.GetBucketLocationAsync(
                It.IsAny<GetBucketLocationRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("bucket unavailable"));
        var check = CreateCheck(s3.Object);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.IsType<AmazonS3Exception>(result.Exception);
    }

    private static AttachmentStorageHealthCheck CreateCheck(IAmazonS3 s3) =>
        new(
            s3,
            Options.Create(new AttachmentObjectStorageOptions
            {
                Enabled = true,
                BucketName = "attachments-test"
            }));
}
