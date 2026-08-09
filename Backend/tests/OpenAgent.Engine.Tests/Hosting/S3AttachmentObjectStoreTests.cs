using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Attachments;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class S3AttachmentObjectStoreTests
{
    [Fact]
    public async Task StoreAsync_WritesPrivateOpaqueTenantPartition()
    {
        PutObjectRequest? captured = null;
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PutObjectResponse { ETag = "\"etag-1\"" });
        var options = Options.Create(new AttachmentObjectStorageOptions
        {
            Enabled = true,
            BucketName = "attachments-test",
            KeyPrefix = "private/attachments"
        });
        var store = new S3AttachmentObjectStore(
            s3.Object,
            options,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero)));
        await using var content = new MemoryStream([1, 2, 3], writable: false);

        AttachmentObjectReference? result = await store.StoreAsync(
            new AttachmentObjectUpload(
                "notes.txt",
                "text/plain",
                "sha-256",
                "tenant-a"),
            content,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("etag-1", result.ETag);
        Assert.NotNull(captured);
        Assert.Equal("attachments-test", captured.BucketName);
        string tenantPartition = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("tenant-a"))).ToLowerInvariant();
        Assert.StartsWith(
            $"private/attachments/{tenantPartition}/2026/08/08/",
            captured.Key,
            StringComparison.Ordinal);
        Assert.Equal(64, tenantPartition.Length);
        Assert.EndsWith(".txt", captured.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", captured.Key, StringComparison.Ordinal);
        Assert.Equal("text/plain", captured.ContentType);
        Assert.Equal("sha-256", captured.Metadata["sha256"]);
    }

    [Fact]
    public async Task DeleteAsync_DeletesFromConfiguredBucket()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.DeleteObjectAsync(
                "attachments-test",
                "private/object.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());
        var store = new S3AttachmentObjectStore(
            s3.Object,
            Options.Create(new AttachmentObjectStorageOptions
            {
                Enabled = true,
                BucketName = "attachments-test"
            }),
            TimeProvider.System);

        await store.DeleteAsync("private/object.txt", CancellationToken.None);

        s3.VerifyAll();
    }

    [Fact]
    public async Task StoreAsync_WhenS3RejectsRequest_MapsDependencyUnavailable()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("bucket unavailable"));
        var store = new S3AttachmentObjectStore(
            s3.Object,
            Options.Create(new AttachmentObjectStorageOptions
            {
                Enabled = true,
                BucketName = "attachments-test"
            }),
            TimeProvider.System);
        await using var content = new MemoryStream([1], writable: false);

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            store.StoreAsync(
                new AttachmentObjectUpload("notes.txt", "text/plain", "sha-256", "tenant-a"),
                content,
                CancellationToken.None));

        Assert.Equal(AgentErrorCode.DependencyUnavailable, exception.ErrorCode);
        Assert.IsType<AmazonS3Exception>(exception.InnerException);
    }

    [Fact]
    public async Task StoreAsync_WithoutTenant_RejectsBeforeCallingS3()
    {
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);
        var store = new S3AttachmentObjectStore(
            s3.Object,
            Options.Create(new AttachmentObjectStorageOptions
            {
                Enabled = true,
                BucketName = "attachments-test"
            }),
            TimeProvider.System);
        await using var content = new MemoryStream([1], writable: false);

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            store.StoreAsync(
                new AttachmentObjectUpload("notes.txt", "text/plain", "sha-256", null),
                content,
                CancellationToken.None));

        Assert.Equal(AgentErrorCode.TenantDataIsolationViolation, exception.ErrorCode);
        s3.VerifyNoOtherCalls();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
