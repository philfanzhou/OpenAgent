using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Host.Attachments;

internal sealed class AgentAttachmentReader
{
    private readonly AgentAttachmentOptions _options;
    private readonly HashSet<string> _allowedExtensions;
    private readonly IAttachmentObjectStore _objectStore;
    private readonly ILogger<AgentAttachmentReader> _logger;

    public AgentAttachmentReader(
        IOptions<AgentAttachmentOptions> options,
        IAttachmentObjectStore objectStore,
        ILogger<AgentAttachmentReader> logger)
    {
        _options = options.Value;
        _allowedExtensions = _options.AllowedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _objectStore = objectStore;
        _logger = logger;
    }

    internal async Task<IReadOnlyList<AgentAttachment>> ReadAsync(
        IFormFileCollection files,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            throw InvalidRequest("At least one attachment is required.");
        }
        if (files.Count > _options.MaxFileCount)
        {
            throw InvalidRequest($"A maximum of {_options.MaxFileCount} attachments is allowed.");
        }

        long declaredTotal = files.Sum(file => file.Length);
        if (declaredTotal > _options.MaxTotalSizeBytes)
        {
            throw InvalidRequest($"Attachment total exceeds {_options.MaxTotalSizeBytes} bytes.");
        }

        List<AgentAttachment> attachments = new(files.Count);
        List<string> storedObjectKeys = [];
        long actualTotal = 0;
        try
        {
            foreach (IFormFile file in files)
            {
                ValidateFile(file);
                await using Stream input = file.OpenReadStream();
                await using MemoryStream buffer = new();
                await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

                actualTotal += buffer.Length;
                if (buffer.Length > _options.MaxFileSizeBytes || actualTotal > _options.MaxTotalSizeBytes)
                {
                    throw InvalidRequest("Attachment data exceeds the configured size limit.");
                }

                byte[] data = buffer.ToArray();
                string sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                string fileName = Path.GetFileName(file.FileName);
                string mediaType = NormalizeMediaType(file.ContentType);
                await using var objectContent = new MemoryStream(data, writable: false);
                AttachmentObjectReference? stored = await _objectStore.StoreAsync(
                    new AttachmentObjectUpload(fileName, mediaType, sha256, tenantId),
                    objectContent,
                    cancellationToken).ConfigureAwait(false);
                if (stored != null)
                {
                    storedObjectKeys.Add(stored.ObjectKey);
                }

                attachments.Add(new AgentAttachment
                {
                    FileName = fileName,
                    MediaType = mediaType,
                    Data = data,
                    ObjectKey = stored?.ObjectKey,
                    Sha256 = sha256
                });
            }
        }
        catch
        {
            await RollbackAsync(storedObjectKeys).ConfigureAwait(false);
            throw;
        }

        return attachments;
    }

    private async Task RollbackAsync(IReadOnlyList<string> objectKeys)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (string objectKey in objectKeys.Reverse())
        {
            try
            {
                await _objectStore.DeleteAsync(objectKey, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AttachmentLog.RollbackDeleteFailed(
                    _logger,
                    exception,
                    objectKey,
                    exception.GetType().FullName ?? exception.GetType().Name);
            }
        }
    }

    private void ValidateFile(IFormFile file)
    {
        if (file.Length <= 0)
        {
            throw InvalidRequest("Empty attachments are not allowed.");
        }
        if (file.Length > _options.MaxFileSizeBytes)
        {
            throw InvalidRequest($"Attachment '{Path.GetFileName(file.FileName)}' exceeds {_options.MaxFileSizeBytes} bytes.");
        }

        string fileName = Path.GetFileName(file.FileName);
        string extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(fileName) || !_allowedExtensions.Contains(extension))
        {
            throw InvalidRequest($"Attachment extension '{extension}' is not allowed.");
        }

        string mediaType = NormalizeMediaType(file.ContentType);
        if (!IsAllowedMediaType(mediaType))
        {
            throw InvalidRequest($"Attachment media type '{mediaType}' is not allowed.");
        }
        if (!MediaTypeMatchesExtension(extension, mediaType))
        {
            throw InvalidRequest(
                $"Attachment media type '{mediaType}' does not match extension '{extension}'.");
        }
    }

    private static bool MediaTypeMatchesExtension(string extension, string mediaType)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" =>
                mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            ".pdf" => mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase),
            ".json" => mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase),
            ".txt" => mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase),
            ".csv" => mediaType.Equals("text/csv", StringComparison.OrdinalIgnoreCase),
            ".md" => mediaType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private bool IsAllowedMediaType(string mediaType)
    {
        foreach (string allowedMediaType in _options.AllowedMediaTypes)
        {
            if (allowedMediaType.EndsWith("/*", StringComparison.Ordinal))
            {
                string prefix = allowedMediaType[..^1];
                if (mediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (mediaType.Equals(allowedMediaType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeMediaType(string? mediaType)
    {
        return string.IsNullOrWhiteSpace(mediaType)
            ? "application/octet-stream"
            : mediaType.Split(';', 2)[0].Trim();
    }

    private static AgentException InvalidRequest(string message)
    {
        return new AgentException(AgentErrorCode.InvalidRequest, message);
    }
}
