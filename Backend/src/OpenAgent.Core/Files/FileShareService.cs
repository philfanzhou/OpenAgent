using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files;

internal sealed class FileShareService(
    IFileAssetService files,
    IFileShareRepository shares,
    IFileObjectStore objectStore,
    IOptions<FileShareOptions> options) : IFileShareService
{
    public async Task<FileShareLink> CreateAsync(
        string fileId,
        FileAssetScope scope,
        FileShareRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "FileId is required.");
        }

        FileAsset? asset = await files.GetAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
        if (asset == null)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, $"File '{fileId}' was not found.");
        }
        if (asset.State != FileAssetState.Ready)
        {
            throw new AgentException(AgentErrorCode.DependencyUnavailable, $"File '{fileId}' is not ready.");
        }
        EnsureTenantObjectKey(asset.ObjectKey, scope.TenantId);

        DateTimeOffset expiresAt = ResolveExpiryAt(request);
        int? maxDownloads = request.Mode == FileShareMode.SingleUse ? 1 : null;
        string token = FileShareTokens.NewToken();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string shareIdHash = FileShareTokens.Hash(token);
        await shares.CreateAsync(
            new FileShareLinkRecord
            {
                ShareIdHash = shareIdHash,
                FileId = asset.FileId,
                TenantId = asset.TenantId,
                OwnerUserId = asset.OwnerUserId,
                ObjectKey = asset.ObjectKey,
                FileName = asset.FileName,
                MediaType = asset.MediaType,
                Length = asset.Length,
                Mode = request.Mode,
                ExpiresAt = expiresAt,
                MaxDownloads = maxDownloads,
                DownloadCount = 0,
                CreatedAt = createdAt
            },
            cancellationToken).ConfigureAwait(false);

        return new FileShareLink
        {
            ShareId = shareIdHash,
            FileId = asset.FileId,
            FileName = asset.FileName,
            MediaType = asset.MediaType,
            Length = asset.Length,
            Mode = request.Mode,
            Url = BuildUrl(token),
            ExpiresAt = expiresAt,
            MaxDownloads = maxDownloads,
            DownloadCount = 0,
            CreatedAt = createdAt
        };
    }

    public async Task<FileShareRedemption?> RedeemAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        FileShareLinkRecord? record = await shares.GetAsync(
            FileShareTokens.Hash(token),
            cancellationToken).ConfigureAwait(false);
        if (record == null || record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        // 先核销再读取：保证“单次下载”在并发和读取失败时都不会被二次使用。
        if (!await shares.TryRedeemAsync(record.ShareIdHash, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        byte[] data = await objectStore.ReadAsync(record.ObjectKey, cancellationToken).ConfigureAwait(false);
        return new FileShareRedemption
        {
            FileName = record.FileName,
            MediaType = record.MediaType,
            Length = record.Length,
            Data = data
        };
    }

    public async Task<IReadOnlyList<FileShareSummary>> ListAsync(
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FileShareLinkRecord> records = await shares.ListByOwnerAsync(
            scope.TenantId,
            scope.UserId,
            cancellationToken).ConfigureAwait(false);
        return records
            .Select(ToSummary)
            .ToList();
    }

    public Task<bool> RevokeAsync(
        string shareIdHash,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(shareIdHash))
        {
            return Task.FromResult(false);
        }

        return shares.DeleteAsync(shareIdHash, scope.TenantId, scope.UserId, cancellationToken);
    }

    private static FileShareSummary ToSummary(FileShareLinkRecord record) => new()
    {
        ShareId = record.ShareIdHash,
        FileId = record.FileId,
        FileName = record.FileName,
        MediaType = record.MediaType,
        Length = record.Length,
        Mode = record.Mode,
        ExpiresAt = record.ExpiresAt,
        MaxDownloads = record.MaxDownloads,
        DownloadCount = record.DownloadCount,
        CreatedAt = record.CreatedAt
    };

    private DateTimeOffset ResolveExpiryAt(FileShareRequest request)
    {
        FileShareOptions settings = options.Value;
        int lifetimeSeconds = request.Mode switch
        {
            FileShareMode.SingleUse => settings.SingleUseLifetimeSeconds,
            FileShareMode.LongTerm => settings.LongTermLifetimeSeconds,
            _ => settings.TemporaryLifetimeSeconds
        };
        if (request.ExpiresInSeconds is not null)
        {
            if (request.ExpiresInSeconds.Value <= 0)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    "ExpiresInSeconds must be greater than zero; permanent shares are not supported.");
            }

            if (request.ExpiresInSeconds.Value > settings.MaxLifetimeSeconds)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    $"Share lifetime cannot exceed {settings.MaxLifetimeSeconds} seconds.");
            }

            lifetimeSeconds = request.ExpiresInSeconds.Value;
        }

        return DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds);
    }

    private string BuildUrl(string token)
    {
        string path = $"{IFileShareService.RoutePrefix}/{token}";
        string? baseUrl = options.Value.PublicBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl) ? path : $"{baseUrl}{path}";
    }

    private static void EnsureTenantObjectKey(string objectKey, string tenantId)
    {
        if (!FileObjectTenantScope.ContainsTenantPartition(objectKey, tenantId))
        {
            throw new TenantDataIsolationException(
                tenantId,
                null,
                "File object storage key is outside the tenant partition.");
        }
    }
}
