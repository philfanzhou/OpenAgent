using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Files;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure;

internal sealed class EfCoreFileAssetRepository(IDbContextFactory<OpenAgentDbContext> contexts) : IFileAssetRepository
{
    public async Task CreateAsync(FileAsset asset, CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.FileAssets.Add(ToEntity(asset));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        FileAssetEntity? entity = await context.FileAssets.SingleOrDefaultAsync(
            item => item.FileId == asset.FileId,
            cancellationToken).ConfigureAwait(false);
        if (entity == null)
        {
            throw new InvalidOperationException($"File asset '{asset.FileId}' was not found.");
        }

        entity.ObjectKey = asset.ObjectKey;
        entity.State = (int)asset.State;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        FileAssetEntity? entity = await context.FileAssets.AsNoTracking().SingleOrDefaultAsync(
            item => item.FileId == fileId,
            cancellationToken).ConfigureAwait(false);
        return entity == null ? null : ToAsset(entity);
    }

    public async Task EnsureConversationReferencesAsync(
        string conversationId,
        IReadOnlyList<string> fileIds,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        string[] distinct = fileIds.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
        {
            return;
        }

        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (string fileId in distinct)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "openagent"."conversation_file_references" ("ConversationId", "FileId", "CreatedAt")
                VALUES ({conversationId}, {fileId}, {createdAt})
                ON CONFLICT ("ConversationId", "FileId") DO NOTHING
                """,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsReferencedAsync(
        string conversationId,
        string fileId,
        CancellationToken cancellationToken)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ConversationFileReferences.AsNoTracking()
            .AnyAsync(item => item.ConversationId == conversationId && item.FileId == fileId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static FileAssetEntity ToEntity(FileAsset asset) => new()
    {
        FileId = asset.FileId,
        TenantId = asset.TenantId,
        OwnerUserId = asset.OwnerUserId,
        FileName = asset.FileName,
        MediaType = asset.MediaType,
        Length = asset.Length,
        Sha256 = asset.Sha256,
        ObjectKey = asset.ObjectKey,
        Source = (int)asset.Source,
        State = (int)asset.State,
        CreatedAt = asset.CreatedAt
    };

    private static FileAsset ToAsset(FileAssetEntity entity) => new()
    {
        FileId = entity.FileId,
        TenantId = entity.TenantId,
        OwnerUserId = entity.OwnerUserId,
        FileName = entity.FileName,
        MediaType = entity.MediaType,
        Length = entity.Length,
        Sha256 = entity.Sha256,
        ObjectKey = entity.ObjectKey,
        Source = (FileAssetSource)entity.Source,
        State = (FileAssetState)entity.State,
        CreatedAt = entity.CreatedAt
    };
}
