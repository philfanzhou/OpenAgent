using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace OpenAgent.Engine.Host.Files;

internal sealed class FileObjectStorageHealthCheck : IHealthCheck
{
    private readonly IAmazonS3 _s3;
    private readonly FileObjectStorageOptions _options;

    public FileObjectStorageHealthCheck(
        IAmazonS3 s3,
        IOptions<FileObjectStorageOptions> options)
    {
        _s3 = s3;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _s3.GetBucketLocationAsync(
                new GetBucketLocationRequest { BucketName = _options.BucketName },
                cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("File object storage is available.");
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            return HealthCheckResult.Unhealthy("File object storage is unavailable.", exception);
        }
    }
}
