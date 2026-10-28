using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Tests.TestDoubles;

/// <summary>
/// In-memory file asset repository recording writes and conversation references.
/// </summary>
internal sealed class RecordingFileAssetRepository : IFileAssetRepository
{
    public Dictionary<string, FileAsset> Assets { get; } = [];
    public HashSet<string> References { get; } = new(StringComparer.Ordinal);

    public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken)
    {
        Assets.Add(asset.FileId, asset);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken)
    {
        Assets[asset.FileId] = asset;
        return Task.CompletedTask;
    }

    public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) =>
        Task.FromResult(Assets.GetValueOrDefault(fileId));

    public Task<IReadOnlyList<FileAsset>> ListReferencedAsync(
        string conversationId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FileAsset>>(Assets.Values
            .Where(asset => References.Contains($"{conversationId}:{asset.FileId}"))
            .ToArray());

    public Task EnsureConversationReferencesAsync(
        string conversationId,
        IReadOnlyList<string> fileIds,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        foreach (string fileId in fileIds)
        {
            References.Add($"{conversationId}:{fileId}");
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsReferencedAsync(
        string conversationId,
        string fileId,
        CancellationToken cancellationToken) =>
        Task.FromResult(References.Contains($"{conversationId}:{fileId}"));
}

/// <summary>
/// Minimal no-op repository for DI wiring tests that never touch file assets.
/// </summary>
internal sealed class EmptyFileAssetRepository : IFileAssetRepository
{
    public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) =>
        Task.FromResult<FileAsset?>(null);
    public Task<IReadOnlyList<FileAsset>> ListReferencedAsync(
        string conversationId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FileAsset>>([]);
    public Task EnsureConversationReferencesAsync(
        string conversationId,
        IReadOnlyList<string> fileIds,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> IsReferencedAsync(
        string conversationId,
        string fileId,
        CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// In-memory object store keeping the last write and read count for assertions.
/// </summary>
internal sealed class RecordingFileObjectStore : IFileObjectStore
{
    public byte[] Content { get; set; } = [];

    /// <summary>Per-key read results; keys without an entry fall back to <see cref="Content"/>.</summary>
    public Dictionary<string, byte[]> ContentsByKey { get; } = new(StringComparer.Ordinal);

    public byte[] LastContent { get; private set; } = [];
    public FileObjectWriteRequest? LastRequest { get; private set; }
    public int ReadCount { get; private set; }

    public async Task<FileObjectReference> WriteAsync(
        FileObjectWriteRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        LastContent = buffer.ToArray();
        Content = LastContent;
        string objectKey = $"files/tenants/{FileObjectTenantScope.CreatePartition(request.TenantId)}/users/{request.UserId}/{request.FileId}";
        ContentsByKey[objectKey] = LastContent;
        return new FileObjectReference
        {
            ObjectKey = objectKey
        };
    }

    public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        ReadCount++;
        return Task.FromResult(ContentsByKey.TryGetValue(objectKey, out byte[]? content) ? content : Content);
    }

    public Task<byte[]> ReadAsync(
        string objectKey,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] content = ContentsByKey.TryGetValue(objectKey, out byte[]? value) ? value : Content;
        if (content.LongLength > maxBytes)
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"File object '{objectKey}' exceeds the configured {maxBytes} byte limit.");
        }

        ReadCount++;
        return Task.FromResult(content);
    }

    public Task<FileObjectAccessReference> CreateReadUrlAsync(
        string objectKey,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new FileObjectAccessReference
        {
            ObjectKey = objectKey,
            Url = $"https://storage.example/{objectKey}",
            ExpiresAt = expiresAt
        });
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// In-memory share link repository mirroring redemption semantics:
/// a redeem only succeeds before expiry and below the download cap.
/// </summary>
internal sealed class RecordingFileShareRepository : IFileShareRepository
{
    public Dictionary<string, FileShareLinkRecord> Records { get; } = new(StringComparer.Ordinal);

    public Task CreateAsync(FileShareLinkRecord record, CancellationToken cancellationToken)
    {
        Records.Add(record.ShareIdHash, record);
        return Task.CompletedTask;
    }

    public Task<FileShareLinkRecord?> GetAsync(string shareIdHash, CancellationToken cancellationToken) =>
        Task.FromResult(Records.GetValueOrDefault(shareIdHash));

    public Task<bool> TryRedeemAsync(string shareIdHash, CancellationToken cancellationToken)
    {
        FileShareLinkRecord? record = Records.GetValueOrDefault(shareIdHash);
        if (record == null
            || record.ExpiresAt <= DateTimeOffset.UtcNow
            || (record.MaxDownloads != null && record.DownloadCount >= record.MaxDownloads.Value))
        {
            return Task.FromResult(false);
        }

        record.DownloadCount++;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<FileShareLinkRecord>> ListByOwnerAsync(
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return Task.FromResult<IReadOnlyList<FileShareLinkRecord>>(
            Records.Values
                .Where(item => item.TenantId == tenantId
                    && item.OwnerUserId == ownerId
                    && item.ExpiresAt > now
                    && (item.MaxDownloads == null || item.DownloadCount < item.MaxDownloads))
                .OrderByDescending(item => item.CreatedAt)
                .ToList());
    }

    public Task<bool> TryRevokeAsync(
        string shareIdHash,
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        FileShareLinkRecord? record = Records.GetValueOrDefault(shareIdHash);
        if (record == null
            || record.TenantId != tenantId
            || record.OwnerUserId != ownerId
            || record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Task.FromResult(false);
        }

        // 软删除：改写失效时间为当前时刻，记录保留。
        record.ExpiresAt = DateTimeOffset.UtcNow;
        return Task.FromResult(true);
    }
}
