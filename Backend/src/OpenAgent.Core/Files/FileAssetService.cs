using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetService : IFileAssetService
{
    private readonly IFileAssetRepository _repository;
    private readonly IFileObjectStore _objectStore;
    private readonly FileAssetOptions _options;

    public FileAssetService(
        IFileAssetRepository repository,
        IFileObjectStore objectStore,
        IOptions<FileAssetOptions> options)
    {
        _repository = repository;
        _objectStore = objectStore;
        _options = options.Value;
    }

    public async Task<FileAsset> UploadAsync(
        FileAssetCreateRequest request,
        Stream content,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateScope(scope);
        byte[] data = await ReadAndValidateAsync(request, content, cancellationToken).ConfigureAwait(false);
        string fileId = Guid.NewGuid().ToString("N");
        string sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        FileAsset pending = new()
        {
            FileId = fileId,
            TenantId = scope.TenantId,
            OwnerUserId = scope.UserId,
            FileName = Path.GetFileName(request.FileName),
            MediaType = NormalizeMediaType(request.MediaType),
            Length = data.LongLength,
            Sha256 = sha256,
            ObjectKey = string.Empty,
            Source = request.Source,
            State = FileAssetState.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _repository.CreateAsync(pending, cancellationToken).ConfigureAwait(false);

        try
        {
            await using var input = new MemoryStream(data, writable: false);
            FileObjectReference stored = await _objectStore.WriteAsync(
                new FileObjectWriteRequest
                {
                    FileId = fileId,
                    TenantId = scope.TenantId,
                    UserId = scope.UserId,
                    FileName = pending.FileName,
                    MediaType = pending.MediaType,
                    Sha256 = sha256
                },
                input,
                cancellationToken).ConfigureAwait(false);
            FileAsset ready = CopyWithStorage(pending, stored.ObjectKey, FileAssetState.Ready);
            await _repository.UpdateAsync(ready, cancellationToken).ConfigureAwait(false);
            return ready;
        }
        catch
        {
            FileAsset failed = CopyWithStorage(pending, string.Empty, FileAssetState.Failed);
            await _repository.UpdateAsync(failed, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return _repository.GetAsync(fileId, cancellationToken);
    }

    public async Task EnsureReferencesAsync(
        IReadOnlyList<string> fileIds,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope.ConversationId))
        {
            return;
        }

        List<string> owned = [];
        foreach (string fileId in fileIds.Distinct(StringComparer.Ordinal))
        {
            FileAsset? asset = await _repository.GetAsync(fileId, cancellationToken).ConfigureAwait(false);
            if (asset != null && IsOwner(asset, scope))
            {
                owned.Add(fileId);
            }
        }

        if (owned.Count > 0)
        {
            await _repository.EnsureConversationReferencesAsync(
                scope.ConversationId,
                owned,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<FileAssetContent> ReadAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateScope(scope);
        FileAsset asset = await GetReadyAssetAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
        byte[] data = await _objectStore.ReadAsync(asset.ObjectKey, cancellationToken).ConfigureAwait(false);
        return new FileAssetContent { Asset = asset, Data = data };
    }

    public async Task<string> ReadTextAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        FileAssetContent content = await ReadAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
        if (!IsTextMediaType(content.Asset.MediaType))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"File '{content.Asset.FileName}' is not a text file.");
        }
        if (content.Data.LongLength > _options.MaxFunctionReadBytes)
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"File '{content.Asset.FileName}' exceeds the function read limit.");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(content.Data);
        }
        catch (DecoderFallbackException exception)
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"File '{content.Asset.FileName}' is not valid UTF-8 text.",
                innerException: exception);
        }
    }

    private async Task<FileAsset> GetReadyAssetAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "FileId is required.");
        }
        if (string.IsNullOrWhiteSpace(scope.ConversationId))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "ConversationId is required for file reads.");
        }

        FileAsset? asset = await _repository.GetAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (asset == null
            || !IsOwner(asset, scope)
            || !await IsReferencedAsync(asset, scope, cancellationToken).ConfigureAwait(false))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, $"File '{fileId}' was not found.");
        }
        if (asset.State != FileAssetState.Ready)
        {
            throw new AgentException(AgentErrorCode.DependencyUnavailable, $"File '{fileId}' is not ready.");
        }

        return asset;
    }

    private static bool IsOwner(FileAsset asset, FileAssetScope scope) =>
        string.Equals(asset.TenantId, scope.TenantId, StringComparison.Ordinal)
        && string.Equals(asset.OwnerUserId, scope.UserId, StringComparison.Ordinal);

    private Task<bool> IsReferencedAsync(
        FileAsset asset,
        FileAssetScope scope,
        CancellationToken cancellationToken) =>
        _repository.IsReferencedAsync(scope.ConversationId!, asset.FileId, cancellationToken);

    private async Task<byte[]> ReadAndValidateAsync(
        FileAssetCreateRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "FileName is required.");
        }

        string fileName = Path.GetFileName(request.FileName);
        string extension = Path.GetExtension(fileName);
        string mediaType = NormalizeMediaType(request.MediaType);
        if (!_options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || !IsAllowedMediaType(mediaType)
            || !MediaTypeMatchesExtension(extension, mediaType))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"File type is not allowed. Supported extensions: " +
                $"{string.Join(", ", _options.AllowedExtensions.Distinct(StringComparer.OrdinalIgnoreCase))}.");
        }

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length == 0 || buffer.Length > _options.MaxFileSizeBytes)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "File size is outside the configured limit.");
        }

        return buffer.ToArray();
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new AgentException(AgentErrorCode.DependencyUnavailable, "File assets are not enabled.");
        }
    }

    private static void ValidateScope(FileAssetScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.TenantId))
        {
            throw new TenantDataIsolationException(null, null, "TenantId is required for file assets.");
        }
        if (string.IsNullOrWhiteSpace(scope.UserId))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "UserId is required for file assets.");
        }
    }

    private bool IsAllowedMediaType(string mediaType) => _options.AllowedMediaTypes.Any(allowed =>
        allowed.EndsWith("/*", StringComparison.Ordinal)
            ? mediaType.StartsWith(allowed[..^1], StringComparison.OrdinalIgnoreCase)
            : mediaType.Equals(allowed, StringComparison.OrdinalIgnoreCase));

    private static bool MediaTypeMatchesExtension(string extension, string mediaType) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" => mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        ".pdf" => mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase),
        ".json" => mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase),
        ".drawio" => mediaType.Equals("application/vnd.jgraph.mxfile", StringComparison.OrdinalIgnoreCase),
        ".txt" => mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase),
        ".csv" => mediaType.Equals("text/csv", StringComparison.OrdinalIgnoreCase),
        ".md" => mediaType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase),
        ".html" or ".htm" => mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsTextMediaType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMediaType(string? mediaType) => string.IsNullOrWhiteSpace(mediaType)
        ? "application/octet-stream"
        : mediaType.Split(';', 2)[0].Trim();

    private static FileAsset CopyWithStorage(FileAsset asset, string objectKey, FileAssetState state) => new()
    {
        FileId = asset.FileId,
        TenantId = asset.TenantId,
        OwnerUserId = asset.OwnerUserId,
        FileName = asset.FileName,
        MediaType = asset.MediaType,
        Length = asset.Length,
        Sha256 = asset.Sha256,
        ObjectKey = objectKey,
        Source = asset.Source,
        State = state,
        CreatedAt = asset.CreatedAt
    };
}
