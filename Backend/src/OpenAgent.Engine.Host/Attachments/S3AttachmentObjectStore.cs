using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Host.Attachments;

internal sealed class S3AttachmentObjectStore : IAttachmentObjectStore
{
    private readonly IAmazonS3 _s3;
    private readonly AttachmentObjectStorageOptions _options;
    private readonly TimeProvider _timeProvider;

    public S3AttachmentObjectStore(
        IAmazonS3 s3,
        IOptions<AttachmentObjectStorageOptions> options,
        TimeProvider timeProvider)
    {
        _s3 = s3;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<AttachmentObjectReference?> StoreAsync(
        AttachmentObjectUpload upload,
        Stream content,
        CancellationToken cancellationToken)
    {
        string tenantPartition = CreateTenantPartition(upload.TenantId);
        string objectKey = CreateObjectKey(upload.FileName, tenantPartition);
        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = upload.MediaType
        };
        request.Metadata["sha256"] = upload.Sha256;
        request.Metadata["tenant-partition"] = tenantPartition;

        try
        {
            PutObjectResponse response = await _s3.PutObjectAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            return new AttachmentObjectReference(
                objectKey,
                response.ETag?.Trim('"'));
        }
        catch (Exception exception)
            when (exception is AmazonServiceException or AmazonClientException)
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                "Attachment object storage is unavailable.",
                innerException: exception);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.DeleteObjectAsync(
                _options.BucketName,
                objectKey,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is AmazonServiceException or AmazonClientException)
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                "Attachment object cleanup failed.",
                innerException: exception);
        }
    }

    private string CreateObjectKey(string fileName, string tenantPartition)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string prefix = _options.KeyPrefix.Trim('/');
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        string generatedName = $"{Guid.NewGuid():N}{extension}";
        string suffix = $"{tenantPartition}/{now:yyyy/MM/dd}/{generatedName}";
        return string.IsNullOrWhiteSpace(prefix) ? suffix : $"{prefix}/{suffix}";
    }

    private static string CreateTenantPartition(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new AgentException(
                AgentErrorCode.TenantDataIsolationViolation,
                "A tenant is required when attachment object storage is enabled.");
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
