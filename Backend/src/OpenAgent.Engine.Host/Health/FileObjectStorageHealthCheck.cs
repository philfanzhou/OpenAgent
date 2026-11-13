using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Framework.ObjectStorage;

namespace OpenAgent.Engine.Host.Health;

internal sealed class FileObjectStorageHealthCheck : IHealthCheck
{
    private readonly IAmazonS3? _s3;
    private readonly FileObjectStorageOptions _options;
    private readonly bool _enabled;

    public FileObjectStorageHealthCheck(
        IOptions<FileAssetOptions> fileAssets,
        IOptions<FileObjectStorageOptions> options,
        IAmazonS3? s3 = null)
    {
        _enabled = fileAssets.Value.Enabled;
        _options = options.Value;
        _s3 = s3;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, object> data = new() { ["enabled"] = _enabled, ["provider"] = "s3" };
        if (!_enabled)
        {
            return HealthCheckResult.Healthy("File object storage is disabled.", data);
        }

        if (_s3 is null)
        {
            return HealthCheckResult.Unhealthy("File object storage is not configured.", data: data);
        }

        try
        {
            await _s3.HeadBucketAsync(
                new HeadBucketRequest { BucketName = _options.BucketName },
                cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("File object storage bucket is accessible.", data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("File object storage is unavailable.", data: data);
        }
    }
}
