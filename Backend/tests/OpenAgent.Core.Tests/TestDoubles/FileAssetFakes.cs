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
        return new FileObjectReference
        {
            ObjectKey = $"files/{request.TenantId}/{request.UserId}/{request.FileId}"
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

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
}
