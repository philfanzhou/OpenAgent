using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Files.Services;

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
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "FileName is required.");
        }
        string fileName = Path.GetFileName(request.FileName);
        ValidateFileNameLength(fileName);
        string mediaType = ResolveMediaType(Path.GetExtension(fileName), request.MediaType);
        // S3 signing and retry need a seekable stream. Buffer non-seekable sources on disk.
        await using FileStream? buffer = content.CanSeek && content.Position == 0 ? null : CreateUploadBuffer();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = await HashUploadAsync(content, buffer, hash, cancellationToken).ConfigureAwait(false);
        string sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        Stream input = buffer ?? content;
        input.Position = 0;
        string fileId = Guid.NewGuid().ToString("N");
        FileAsset pending = new()
        {
            FileId = fileId,
            TenantId = scope.TenantId,
            OwnerUserId = scope.UserId,
            FileName = fileName,
            RuntimeOutputPath = request.RuntimeOutputPath,
            MediaType = mediaType,
            Length = length,
            Sha256 = sha256,
            ObjectKey = string.Empty,
            Source = request.Source,
            State = FileAssetState.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _repository.CreateAsync(pending, cancellationToken).ConfigureAwait(false);

        try
        {
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

    public async Task CopyReferencedToAsync(string fileId, FileAssetScope scope, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateScope(scope);
        FileAsset asset = await GetReadyAssetAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
        EnsureTenantObjectKey(asset.ObjectKey, scope.TenantId);
        await _objectStore.CopyToAsync(asset.ObjectKey, destination, Math.Min(maxBytes, _options.MaxFileSizeBytes), cancellationToken).ConfigureAwait(false);
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
            throw new AgentException(AgentErrorCode.NotFound, $"File '{fileId}' was not found.");
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

    private async Task<long> HashUploadAsync(Stream content, FileStream? buffer, IncrementalHash hash, CancellationToken cancellationToken)
    {
        long length = 0;
        byte[] chunk = new byte[65536];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (read > _options.MaxFileSizeBytes - length)
            {
                throw InvalidFileSize();
            }
            hash.AppendData(chunk, 0, read);
            if (buffer is not null)
            {
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            length += read;
        }
        return length == 0 ? throw InvalidFileSize() : length;
    }

    private static FileStream CreateUploadBuffer()
    {
        FileStreamOptions options = new()
        {
            Access = FileAccess.ReadWrite,
            Mode = FileMode.CreateNew,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(Path.Combine(Path.GetTempPath(), $"openagent-upload-{Guid.NewGuid():N}"), options);
    }

    internal static void ValidateFileNameLength(string fileName)
    {
        if (fileName.Length > FileAsset.MaxFileNameLength && fileName.EnumerateRunes().Count() > FileAsset.MaxFileNameLength)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, $"FileName must not exceed {FileAsset.MaxFileNameLength} characters.");
        }
    }

    private static AgentException InvalidFileSize() =>
        new(AgentErrorCode.InvalidRequest, "File size is outside the configured limit.");

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

    private static string ResolveMediaType(string extension, string? requestedMediaType)
    {
        string normalized = NormalizeMediaType(requestedMediaType);
        return IsGenericMediaType(normalized) && FileMediaTypeCatalog.TryGetMediaType(extension, out string inferred)
            ? inferred
            : normalized;
    }

    private static bool IsGenericMediaType(string mediaType) =>
        mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            // Windows browsers report x-zip-compressed for .zip uploads instead of
            // the canonical type or octet-stream.
            || mediaType.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextMediaType(string mediaType) => FileMediaTypeCatalog.IsTextMediaType(mediaType);

    private static string NormalizeMediaType(string? mediaType) => string.IsNullOrWhiteSpace(mediaType)
        ? "application/octet-stream"
        : mediaType.Split(';', 2)[0].Trim();

    private static FileAsset CopyWithStorage(FileAsset asset, string objectKey, FileAssetState state) => new()
    {
        FileId = asset.FileId,
        TenantId = asset.TenantId,
        OwnerUserId = asset.OwnerUserId,
        FileName = asset.FileName,
        RuntimeOutputPath = asset.RuntimeOutputPath,
        MediaType = asset.MediaType,
        Length = asset.Length,
        Sha256 = asset.Sha256,
        ObjectKey = objectKey,
        Source = asset.Source,
        State = state,
        CreatedAt = asset.CreatedAt
    };
}
