using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Files;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure;

internal sealed class EfCoreFileShareRepository(IDbContextFactory<OpenAgentDbContext> contexts) : IFileShareRepository
{
    public async Task CreateAsync(FileShareLinkRecord record, CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.FileShareLinks.Add(ToEntity(record));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileShareLinkRecord?> GetAsync(string shareIdHash, CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        FileShareLinkEntity? entity = await context.FileShareLinks.AsNoTracking().SingleOrDefaultAsync(
            item => item.ShareIdHash == shareIdHash,
            cancellationToken).ConfigureAwait(false);
        return entity == null ? null : ToRecord(entity);
    }

    public async Task<bool> TryRedeemAsync(string shareIdHash, CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int redeemed = await context.FileShareLinks
            .Where(item => item.ShareIdHash == shareIdHash
                && item.ExpiresAt > now
                && (item.MaxDownloads == null || item.DownloadCount < item.MaxDownloads))
            .ExecuteUpdateAsync(
                update => update.SetProperty(item => item.DownloadCount, item => item.DownloadCount + 1),
                cancellationToken).ConfigureAwait(false);
        return redeemed == 1;
    }

    public async Task<IReadOnlyList<FileShareLinkRecord>> ListByOwnerAsync(
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<FileShareLinkEntity> entities = await context.FileShareLinks.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.OwnerUserId == ownerId)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<bool> DeleteAsync(
        string shareIdHash,
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        int deleted = await context.FileShareLinks
            .Where(item => item.ShareIdHash == shareIdHash
                && item.TenantId == tenantId
                && item.OwnerUserId == ownerId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return deleted == 1;
    }

    private static FileShareLinkEntity ToEntity(FileShareLinkRecord record) => new()
    {
        ShareIdHash = record.ShareIdHash,
        FileId = record.FileId,
        TenantId = record.TenantId,
        OwnerUserId = record.OwnerUserId,
        ObjectKey = record.ObjectKey,
        FileName = record.FileName,
        MediaType = record.MediaType,
        Length = record.Length,
        Mode = (int)record.Mode,
        ExpiresAt = record.ExpiresAt,
        MaxDownloads = record.MaxDownloads,
        DownloadCount = record.DownloadCount,
        CreatedAt = record.CreatedAt
    };

    private static FileShareLinkRecord ToRecord(FileShareLinkEntity entity) => new()
    {
        ShareIdHash = entity.ShareIdHash,
        FileId = entity.FileId,
        TenantId = entity.TenantId,
        OwnerUserId = entity.OwnerUserId,
        ObjectKey = entity.ObjectKey,
        FileName = entity.FileName,
        MediaType = entity.MediaType,
        Length = entity.Length,
        Mode = (FileShareMode)entity.Mode,
        ExpiresAt = entity.ExpiresAt,
        MaxDownloads = entity.MaxDownloads,
        DownloadCount = entity.DownloadCount,
        CreatedAt = entity.CreatedAt
    };
}
