using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetService : IFileAssetService
{
    private static readonly TimeSpan TransferUrlLifetime = TimeSpan.FromMinutes(15);

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

    public async Task<FileAsset?> GetAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateScope(scope);
        FileAsset? asset = await _repository.GetAsync(fileId, cancellationToken).ConfigureAwait(false);
        return asset != null && IsOwner(asset, scope) ? asset : null;
    }

    public async Task<FileAsset?> GetReferencedAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateScope(scope);
        if (string.IsNullOrWhiteSpace(scope.ConversationId)
            || string.IsNullOrWhiteSpace(fileId))
        {
            return null;
        }

        FileAsset? asset = await _repository.GetAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (asset == null
            || !IsOwner(asset, scope)
            || asset.State != FileAssetState.Ready
            || !await IsReferencedAsync(asset, scope, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return asset;
    }

    public async Task<IReadOnlyList<FileAsset>> ListAsync(
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateScope(scope);
        if (string.IsNullOrWhiteSpace(scope.ConversationId))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "ConversationId is required to list conversation files.");
        }

        IReadOnlyList<FileAsset> assets = await _repository.ListReferencedAsync(
            scope.ConversationId,
            cancellationToken).ConfigureAwait(false);
        return assets
            .Where(asset => IsOwner(asset, scope))
            .ToArray();
    }

    public async Task<FileObjectAccessReference> CreateTransferUrlAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateScope(scope);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "FileId is required.");
        }

        FileAsset? asset = await _repository.GetAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (asset == null || !IsOwner(asset, scope))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, $"File '{fileId}' was not found.");
        }
        if (asset.State != FileAssetState.Ready)
        {
            throw new AgentException(AgentErrorCode.DependencyUnavailable, $"File '{fileId}' is not ready.");
        }

        EnsureTenantObjectKey(asset.ObjectKey, scope.TenantId);
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(TransferUrlLifetime);
        return await _objectStore.CreateReadUrlAsync(
            asset.ObjectKey,
            expiresAt,
            cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        EnsureEnabled();
        ValidateScope(scope);
        FileAsset asset = await GetReadyAssetAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
        EnsureTenantObjectKey(asset.ObjectKey, scope.TenantId);
        byte[] data = await ReadObjectBytesAsync(
            asset.ObjectKey,
            maxBytes ?? _options.MaxFileSizeBytes,
            cancellationToken).ConfigureAwait(false);
        return new FileAssetContent { Asset = asset, Data = data };
    }

    public async Task<string> ReadTextAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        FileAssetContent content = await ReadAsync(
            fileId,
            scope,
            cancellationToken,
            _options.MaxFunctionReadBytes).ConfigureAwait(false);
        if (!IsTextMediaType(content.Asset.MediaType))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"File '{content.Asset.FileName}' is not a text file.");
        }

        return DecodeFunctionText(content.Data, content.Asset.FileName);
    }

    public async Task<string> ReadObjectTextAsync(
        string objectKey,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        byte[] data = await ReadObjectAsync(
            objectKey,
            scope,
            cancellationToken,
            _options.MaxFunctionReadBytes).ConfigureAwait(false);
        return DecodeFunctionText(data, objectKey);
    }

    private string DecodeFunctionText(byte[] data, string displayName)
    {
        if (data.LongLength > _options.MaxFunctionReadBytes)
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"File '{displayName}' exceeds the function read limit.");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException exception)
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"File '{displayName}' is not valid UTF-8 text.",
                innerException: exception);
        }
    }

    public async Task<byte[]> ReadObjectAsync(
        string objectKey,
        FileAssetScope scope,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        EnsureEnabled();
        ValidateScope(scope);
        string normalized = NormalizeObjectKey(objectKey);
        EnsureTenantObjectKey(normalized, scope.TenantId);
        return await ReadObjectBytesAsync(
            normalized,
            maxBytes ?? _options.MaxFileSizeBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileArchiveResult> CompressAsync(
        FileArchiveRequest request,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateScope(scope);
        if (string.IsNullOrWhiteSpace(request.OutputName))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "OutputName is required.");
        }
        string outputName = Path.GetFileName(request.OutputName);
        if (!string.Equals(Path.GetExtension(outputName), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "Archive output name must end with .zip.");
        }
        IReadOnlyList<FileArchiveItem> items = request.Items;
        if (items.Count == 0)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "At least one archive item is required.");
        }
        if (items.Count > _options.MaxArchiveFileCount)
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"Archive cannot contain more than {_options.MaxArchiveFileCount} files.");
        }

        var entries = new List<(string EntryName, byte[] Data)>(items.Count);
        var entryNames = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (FileArchiveItem item in items)
        {
            long remainingBytes = _options.MaxArchiveInputBytes - totalBytes;
            if (remainingBytes <= 0)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    $"Archive input exceeds the {_options.MaxArchiveInputBytes} byte limit.");
            }

            (string entryName, byte[] data) = await ReadArchiveItemAsync(
                item,
                scope,
                remainingBytes,
                cancellationToken).ConfigureAwait(false);
            totalBytes = checked(totalBytes + data.LongLength);
            if (totalBytes > _options.MaxArchiveInputBytes)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    $"Archive input exceeds the {_options.MaxArchiveInputBytes} byte limit.");
            }
            if (!entryNames.Add(entryName))
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    $"Archive contains duplicate entry name '{entryName}'.");
            }
            entries.Add((entryName, data));
        }

        byte[] archive = BuildZipArchive(entries);
        string fileId = $"archive-{Guid.NewGuid():N}";
        string sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        FileAsset pending = new()
        {
            FileId = fileId,
            TenantId = scope.TenantId,
            OwnerUserId = scope.UserId,
            FileName = outputName,
            MediaType = "application/zip",
            Length = archive.LongLength,
            Sha256 = sha256,
            ObjectKey = string.Empty,
            Source = FileAssetSource.Agent,
            State = FileAssetState.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _repository.CreateAsync(pending, cancellationToken).ConfigureAwait(false);

        FileAsset asset;
        try
        {
            await using var input = new MemoryStream(archive, writable: false);
            FileObjectReference stored = await _objectStore.WriteAsync(
                new FileObjectWriteRequest
                {
                    FileId = fileId,
                    TenantId = scope.TenantId,
                    UserId = scope.UserId,
                    FileName = outputName,
                    MediaType = "application/zip",
                    Sha256 = sha256
                },
                input,
                cancellationToken).ConfigureAwait(false);
            asset = CopyWithStorage(pending, stored.ObjectKey, FileAssetState.Ready);
            await _repository.UpdateAsync(asset, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _repository.UpdateAsync(
                CopyWithStorage(pending, string.Empty, FileAssetState.Failed),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new FileArchiveResult
        {
            Asset = asset,
            ObjectKey = asset.ObjectKey,
            Length = asset.Length,
            FileCount = entries.Count
        };
    }

    private async Task<(string EntryName, byte[] Data)> ReadArchiveItemAsync(
        FileArchiveItem item,
        FileAssetScope scope,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        bool hasFileId = !string.IsNullOrWhiteSpace(item.FileId);
        bool hasObjectKey = !string.IsNullOrWhiteSpace(item.ObjectKey);
        if (hasFileId == hasObjectKey)
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "Each archive item must provide exactly one of fileId or objectKey.");
        }

        if (hasFileId)
        {
            FileAssetContent content = await ReadAsync(
                item.FileId!,
                scope,
                cancellationToken,
                maxBytes).ConfigureAwait(false);
            return (NormalizeArchiveEntryName(item.FileName ?? content.Asset.FileName), content.Data);
        }

        byte[] data = await ReadObjectAsync(
            item.ObjectKey!,
            scope,
            cancellationToken,
            maxBytes).ConfigureAwait(false);
        return (NormalizeArchiveEntryName(item.FileName ?? Path.GetFileName(item.ObjectKey) ?? string.Empty), data);
    }

    private Task<byte[]> ReadObjectBytesAsync(
        string objectKey,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        return _objectStore.ReadAsync(objectKey, maxBytes, cancellationToken);
    }

    private string NormalizeArchiveEntryName(string value)
    {
        string normalized = value.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".." || segment.Contains('\0')))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                $"Archive entry name '{value}' is invalid.");
        }
        return normalized;
    }

    private static byte[] BuildZipArchive(IReadOnlyList<(string EntryName, byte[] Data)> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string entryName, byte[] data) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                entryStream.Write(data, 0, data.Length);
            }
        }
        return output.ToArray();
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

    private static string NormalizeObjectKey(string value)
    {
        string normalized = value.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "Object storage key is invalid.");
        }
        return normalized;
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
        ".zip" => mediaType.Equals("application/zip", StringComparison.OrdinalIgnoreCase),
        ".pptx" => mediaType.Equals("application/vnd.openxmlformats-officedocument.presentationml.presentation", StringComparison.OrdinalIgnoreCase),
        ".xlsx" => mediaType.Equals("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase),
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
