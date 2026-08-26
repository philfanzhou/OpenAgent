using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Files;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class S3FileObjectStoreTests
{
    [Fact]
    public async Task WriteAsync_UsesOpaqueTenantPartitionAndFileId()
    {
        PutObjectRequest? captured = null;
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PutObjectResponse());
        var store = new S3FileObjectStore(
            s3.Object,
            Options.Create(new FileObjectStorageOptions { BucketName = "files-test", KeyPrefix = "private/files" }));
        await using var content = new MemoryStream([1, 2, 3], writable: false);

        FileObjectReference result = await store.WriteAsync(
            new FileObjectWriteRequest
            {
                FileId = "file-a",
                TenantId = "tenant-a",
                UserId = "user-a",
                FileName = "report.md",
                MediaType = "text/markdown",
                Sha256 = "sha-256"
            },
            content,
            CancellationToken.None);

        string tenantHash = FileObjectTenantScope.CreatePartition("tenant-a");
        string userHash = FileObjectTenantScope.CreatePartition("user-a");
        Assert.Equal($"private/files/tenants/{tenantHash}/users/{userHash}/file-a.md", result.ObjectKey);
        Assert.NotNull(captured);
        Assert.Equal("files-test", captured.BucketName);
        Assert.Equal("sha-256", captured.Metadata["sha256"]);
        Assert.DoesNotContain("tenant-a", captured.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("user-a", captured.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_TenantScopeDoesNotIncludeUserPartition()
    {
        PutObjectRequest? captured = null;
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PutObjectResponse());
        var store = new S3FileObjectStore(
            s3.Object,
            Options.Create(new FileObjectStorageOptions { BucketName = "files-test", KeyPrefix = "private/files" }));
        await using var content = new MemoryStream([1, 2, 3], writable: false);

        FileObjectReference result = await store.WriteAsync(
            new FileObjectWriteRequest
            {
                FileId = "skill-file",
                TenantId = "tenant-a",
                UserId = "uploader-a",
                Scope = FileObjectScope.Tenant,
                FileName = "SKILL.md",
                MediaType = "text/markdown",
                Sha256 = "sha-256",
                ObjectKeyPrefix = "skill-packages/skill-1"
            },
            content,
            CancellationToken.None);

        string tenantHash = FileObjectTenantScope.CreatePartition("tenant-a");
        Assert.Equal($"private/files/tenants/{tenantHash}/skill-packages/skill-1/SKILL.md", result.ObjectKey);
        Assert.NotNull(captured);
        Assert.DoesNotContain("/users/", captured.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("uploader-a", captured.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_WithLimitRejectsOversizedResponseBeforeBuffering()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.GetObjectAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ContentLength = 5,
                ResponseStream = new MemoryStream([1, 2, 3, 4, 5], writable: false)
            });
        var store = new S3FileObjectStore(
            s3.Object,
            Options.Create(new FileObjectStorageOptions { BucketName = "files-test" }));

        await Assert.ThrowsAsync<AgentException>(() => store.ReadAsync(
            "tenant/object.txt",
            4,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_WithLimitAcceptsObjectExactlyAtLimit()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.GetObjectAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ContentLength = 4,
                ResponseStream = new MemoryStream([1, 2, 3, 4], writable: false)
            });
        var store = new S3FileObjectStore(
            s3.Object,
            Options.Create(new FileObjectStorageOptions { BucketName = "files-test" }));

        byte[] result = await store.ReadAsync("tenant/object.txt", 4, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], result);
    }

    [Fact]
    public async Task CreateReadUrlAsync_UsesBucketAndObjectKey()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(client => client.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
            .Returns("https://storage.example/signed-file");
        var store = new S3FileObjectStore(
            s3.Object,
            Options.Create(new FileObjectStorageOptions { BucketName = "files-test", KeyPrefix = "private/files" }));
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        FileObjectAccessReference result = await store.CreateReadUrlAsync(
            "private/files/tenants/tenant-hash/file-a.pdf",
            expiresAt,
            CancellationToken.None);

        Assert.Equal("https://storage.example/signed-file", result.Url);
        Assert.Equal("private/files/tenants/tenant-hash/file-a.pdf", result.ObjectKey);
        GetPreSignedUrlRequest request = s3.Invocations
            .Single(invocation => invocation.Method.Name == nameof(IAmazonS3.GetPreSignedURL))
            .Arguments[0] as GetPreSignedUrlRequest
            ?? throw new Xunit.Sdk.XunitException("Presign request was not captured.");
        Assert.Equal("files-test", request.BucketName);
        Assert.Equal(result.ObjectKey, request.Key);
        Assert.Equal(HttpVerb.GET, request.Verb);
    }
}
