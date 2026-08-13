using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Files;
using OpenAgent.Engine.Host.Files;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class S3FileObjectStoreTests
{
    [Theory]
    [InlineData("user-a")]
    [InlineData("anonymous")]
    public async Task WriteAsync_UsesOpaqueTenantUserAndConversationPartitions(string userId)
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
                UserId = userId,
                ConversationId = "conversation-a",
                FileName = "report.md",
                MediaType = "text/markdown",
                Sha256 = "sha-256"
            },
            content,
            CancellationToken.None);

        string tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("tenant-a"))).ToLowerInvariant();
        string userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
        string conversationHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("conversation-a"))).ToLowerInvariant();
        Assert.Equal(
            $"private/files/tenants/{tenantHash}/users/{userHash}/conversations/{conversationHash}/file-a.md",
            result.ObjectKey);
        Assert.NotNull(captured);
        Assert.Equal("files-test", captured.BucketName);
        Assert.Equal("sha-256", captured.Metadata["sha256"]);
        Assert.DoesNotContain("tenant-a", captured.Key, StringComparison.Ordinal);
        Assert.DoesNotContain(userId, captured.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("conversation-a", captured.Key, StringComparison.Ordinal);
    }
}
