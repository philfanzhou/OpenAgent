using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// Inline file transfer between OpenAgent FileAssets and MCP JSON tool contracts.
/// MCP has no standard file-argument encoding; the embedded-resource blob object is the
/// protocol's only standard bytes-in-JSON container, so it is used for inputs and outputs
/// alike. Tool schemas pass through unchanged — the model is told via the tool description
/// that file arguments accept OpenAgent fileIds.
/// </summary>
internal static class McpInlineFileTransfer
{
    internal static async Task ResolveArgumentsAsync(
        IDictionary<string, object?> arguments,
        IFileAssetService files,
        FileAssetScope scope,
        bool legacyBase64,
        CancellationToken cancellationToken)
    {
        // Keys are snapshotted because matching entries are replaced in place.
        foreach (string key in arguments.Keys.ToArray())
        {
            string? candidate = ReadString(arguments[key]);
            if (!LooksLikeFileId(candidate))
            {
                continue;
            }

            FileAsset? asset = await files.GetReferencedAsync(candidate!, scope, cancellationToken)
                .ConfigureAwait(false);
            if (asset == null)
            {
                // Not a file referenced by this conversation; leave the original value untouched.
                continue;
            }

            FileAssetContent content = await files.ReadAsync(candidate!, scope, cancellationToken)
                .ConfigureAwait(false);
            arguments[key] = legacyBase64
                ? Convert.ToBase64String(content.Data)
                : CreateResourcePayload(content);
        }
    }

    internal static async Task<JsonElement> CaptureResultFilesAsync(
        CallToolResult result,
        IFileAssetService files,
        FileAssetScope scope,
        FileAssetExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        if (JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions)
            is not JsonObject root
            || root["content"] is not JsonArray content)
        {
            return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
        }

        JsonArray captured = [];
        for (int index = 0; index < content.Count; index++)
        {
            if (content[index] is not JsonObject block)
            {
                continue;
            }

            JsonObject? descriptor = await TryCaptureBlockAsync(
                block,
                index,
                files,
                scope,
                executionContext,
                cancellationToken).ConfigureAwait(false);
            if (descriptor != null)
            {
                // The descriptor instance goes into the content array; the clone keeps a
                // copy in the top-level files list without re-parenting the same node.
                content[index] = descriptor;
                captured.Add(descriptor.DeepClone());
            }
        }

        if (captured.Count > 0)
        {
            root["files"] = captured;
        }
        return JsonSerializer.SerializeToElement(root, McpJsonUtilities.DefaultOptions);
    }

    private static async Task<JsonObject?> TryCaptureBlockAsync(
        JsonObject block,
        int index,
        IFileAssetService files,
        FileAssetScope scope,
        FileAssetExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        string? type = block["type"]?.GetValue<string>();
        string? base64 = null;
        string? mediaType = null;
        string? uri = null;
        switch (type)
        {
            case "image" or "audio":
                base64 = block["data"]?.GetValue<string>();
                mediaType = block["mimeType"]?.GetValue<string>();
                break;
            case "resource" when block["resource"] is JsonObject resource:
                base64 = resource["blob"]?.GetValue<string>();
                mediaType = resource["mimeType"]?.GetValue<string>();
                uri = resource["uri"]?.GetValue<string>();
                break;
            default:
                return null;
        }

        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        byte[] data = Convert.FromBase64String(base64);
        string normalizedMediaType = string.IsNullOrWhiteSpace(mediaType)
            ? "application/octet-stream"
            : mediaType;
        await using MemoryStream input = new(data, writable: false);
        FileAsset asset = await files.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = ResolveFileName(uri, normalizedMediaType, index),
                MediaType = normalizedMediaType,
                Source = FileAssetSource.Agent
            },
            input,
            scope,
            cancellationToken).ConfigureAwait(false);
        await files.EnsureReferencesAsync([asset.FileId], scope, cancellationToken).ConfigureAwait(false);
        executionContext.RecordPublished(asset);
        return new JsonObject
        {
            ["type"] = "file",
            ["fileId"] = asset.FileId,
            ["fileName"] = asset.FileName,
            ["mediaType"] = asset.MediaType,
            ["length"] = asset.Length
        };
    }

    private static JsonObject CreateResourcePayload(FileAssetContent file) => new()
    {
        ["type"] = "resource",
        ["resource"] = new JsonObject
        {
            ["uri"] = $"openagent://file/{Uri.EscapeDataString(file.Asset.FileId)}",
            ["mimeType"] = file.Asset.MediaType,
            ["blob"] = Convert.ToBase64String(file.Data)
        }
    };

    /// <summary>
    /// Cheap pre-filter before the metadata lookup. OpenAgent fileIds are GUIDs ("N" format,
    /// optionally "archive-" prefixed); URLs, prose and other identifiers fail this check
    /// without touching the repository.
    /// </summary>
    private static bool LooksLikeFileId(string? value)
    {
        if (value is not { Length: >= 8 and <= 128 })
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character) && character != '-')
            {
                return false;
            }
        }
        return true;
    }

    private static string? ReadString(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null
    };

    private static string ResolveFileName(string? uri, string mediaType, int index)
    {
        if (!string.IsNullOrWhiteSpace(uri)
            && Uri.TryCreate(uri, UriKind.RelativeOrAbsolute, out Uri? parsed))
        {
            string path = parsed.IsAbsoluteUri ? parsed.AbsolutePath : parsed.OriginalString;
            string candidate = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(candidate) && candidate != "/")
            {
                return candidate;
            }
        }

        string extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "application/pdf" => ".pdf",
            "application/json" => ".json",
            "application/zip" => ".zip",
            "text/csv" => ".csv",
            "text/markdown" => ".md",
            "text/plain" => ".txt",
            _ => ".bin"
        };
        return $"mcp-output-{index + 1}{extension}";
    }
}
