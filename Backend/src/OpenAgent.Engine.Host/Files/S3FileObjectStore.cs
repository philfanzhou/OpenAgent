using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Host.Files;

internal sealed class S3FileObjectStore : IFileObjectStore
{
    private readonly IAmazonS3 _s3;
    private readonly FileObjectStorageOptions _options;

    public S3FileObjectStore(IAmazonS3 s3, IOptions<FileObjectStorageOptions> options)
    {
        _s3 = s3;
        _options = options.Value;
    }

    public async Task<FileObjectReference> WriteAsync(
        FileObjectWriteRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        string objectKey = CreateObjectKey(request);
        var put = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = request.MediaType
        };
        put.Metadata["sha256"] = request.Sha256;

        try
        {
            await _s3.PutObjectAsync(put, cancellationToken).ConfigureAwait(false);
            return new FileObjectReference { ObjectKey = objectKey };
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException)
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                "File object storage is unavailable.",
                innerException: exception);
        }
    }

    public async Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            using GetObjectResponse response = await _s3.GetObjectAsync(
                _options.BucketName,
                objectKey,
                cancellationToken).ConfigureAwait(false);
            await using Stream input = response.ResponseStream;
            await using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException)
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                "File object storage is unavailable.",
                innerException: exception);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.DeleteObjectAsync(_options.BucketName, objectKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException)
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                "File object cleanup failed.",
                innerException: exception);
        }
    }

    private string CreateObjectKey(FileObjectWriteRequest request)
    {
        string root = $"{_options.KeyPrefix.Trim('/')}/tenants/{FileObjectTenantScope.CreatePartition(request.TenantId)}" +
            $"/users/{FileObjectTenantScope.CreatePartition(request.UserId)}";
        if (!string.IsNullOrWhiteSpace(request.ObjectKeyPrefix))
        {
            return $"{root}/{NormalizePath(request.ObjectKeyPrefix!)}/{NormalizePath(request.FileName)}";
        }

        string extension = Path.GetExtension(request.FileName).ToLowerInvariant();
        return $"{root}/{request.FileId}{extension}";
    }

    private static string NormalizePath(string value)
    {
        string normalized = value.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException("Object storage path is invalid.");
        }
        return normalized;
    }
}
