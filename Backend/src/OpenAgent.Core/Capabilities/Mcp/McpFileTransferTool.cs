using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// Opt-in adapter for MCP tools whose contract carries file bytes in JSON.
/// MCP tool arguments remain JSON; this adapter only replaces a configured fileId
/// argument with base64 or an embedded resource object and persists binary result blocks.
/// </summary>
internal sealed class McpFileTransferTool : AIFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly McpClientTool _inner;
    private readonly IReadOnlyList<McpFileTransferBinding> _bindings;
    private readonly IFileAssetService _files;
    private readonly FileAssetExecutionContext _executionContext;
    private readonly JsonElement _schema;

    private McpFileTransferTool(
        McpClientTool inner,
        IReadOnlyList<McpFileTransferBinding> bindings,
        IFileAssetService files,
        FileAssetExecutionContext executionContext)
    {
        _inner = inner;
        _bindings = bindings;
        _files = files;
        _executionContext = executionContext;
        _schema = BuildSchema(inner.JsonSchema, bindings);
    }

    internal static AITool Create(
        McpClientTool inner,
        IReadOnlyList<McpFileTransferBinding> bindings,
        IFileAssetService files,
        FileAssetExecutionContext executionContext) =>
        new McpFileTransferTool(
            inner,
            bindings,
            files,
            executionContext);

    public override string Name => _inner.Name;

    public override string Description =>
        _inner.Description
        + " When a configured file argument is needed, provide the OpenAgent fileId; "
        + "the platform supplies the MCP wire representation. Binary result blocks are "
        + "registered as downloadable OpenAgent files.";

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> callArguments = [];
        foreach (KeyValuePair<string, object?> argument in arguments)
        {
            callArguments[argument.Key] = argument.Value;
        }

        foreach (McpFileTransferBinding binding in _bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.ArgumentName)
                || !callArguments.TryGetValue(binding.ArgumentName, out object? value))
            {
                continue;
            }

            string? fileId = ReadString(value);
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new InvalidOperationException(
                    $"MCP file argument '{binding.ArgumentName}' must contain an OpenAgent fileId.");
            }

            FileAssetContent file = await _files.ReadAsync(
                fileId,
                RequireScope(),
                cancellationToken).ConfigureAwait(false);
            callArguments[binding.ArgumentName] = binding.InputMode switch
            {
                McpFileInputMode.Base64 => Convert.ToBase64String(file.Data),
                McpFileInputMode.Resource => CreateResourcePayload(file),
                _ => throw new InvalidOperationException(
                    $"Unsupported MCP file input mode '{binding.InputMode}'.")
            };
        }

        CallToolResult result = await _inner.CallAsync(
            callArguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!_bindings.Any(binding => binding.CaptureBinaryResults))
        {
            return JsonSerializer.SerializeToElement(result, _inner.JsonSerializerOptions);
        }

        List<object> content = [];
        List<object> files = [];
        int fileIndex = 0;
        foreach (ContentBlock block in result.Content ?? [])
        {
            switch (block)
            {
                case TextContentBlock text:
                    content.Add(new { type = "text", text = text.Text });
                    break;
                case ImageContentBlock image:
                    content.Add(await PersistBinaryAsync(
                        image.DecodedData.ToArray(),
                        image.MimeType,
                        null,
                        fileIndex++,
                        files,
                        cancellationToken).ConfigureAwait(false));
                    break;
                case AudioContentBlock audio:
                    content.Add(await PersistBinaryAsync(
                        audio.DecodedData.ToArray(),
                        audio.MimeType,
                        null,
                        fileIndex++,
                        files,
                        cancellationToken).ConfigureAwait(false));
                    break;
                case EmbeddedResourceBlock resource when resource.Resource is BlobResourceContents blob:
                    content.Add(await PersistBinaryAsync(
                        blob.DecodedData.ToArray(),
                        blob.MimeType,
                        blob.Uri,
                        fileIndex++,
                        files,
                        cancellationToken).ConfigureAwait(false));
                    break;
                case EmbeddedResourceBlock resource when resource.Resource is TextResourceContents text:
                    content.Add(new { type = "text", text = text.Text, uri = text.Uri });
                    break;
                case ResourceLinkBlock link:
                    content.Add(new
                    {
                        type = "resource_link",
                        uri = link.Uri,
                        name = link.Name,
                        title = link.Title,
                        description = link.Description,
                        mimeType = link.MimeType,
                        size = link.Size
                    });
                    break;
                default:
                    content.Add(new { type = block.Type });
                    break;
            }
        }

        return JsonSerializer.SerializeToElement(
            new
            {
                isError = result.IsError,
                content,
                structuredContent = result.StructuredContent,
                files
            },
            JsonOptions);
    }

    private async Task<object> PersistBinaryAsync(
        byte[] data,
        string? mediaType,
        string? uri,
        int index,
        List<object> files,
        CancellationToken cancellationToken)
    {
        string normalizedMediaType = string.IsNullOrWhiteSpace(mediaType)
            ? "application/octet-stream"
            : mediaType;
        string fileName = ResolveFileName(uri, normalizedMediaType, index);
        await using MemoryStream input = new(data, writable: false);
        FileAsset asset = await _files.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = fileName,
                MediaType = normalizedMediaType,
                Source = FileAssetSource.Agent
            },
            input,
            RequireScope(),
            cancellationToken).ConfigureAwait(false);
        await _files.EnsureReferencesAsync(
            [asset.FileId],
            RequireScope(),
            cancellationToken).ConfigureAwait(false);
        _executionContext.RecordPublished(asset);
        var descriptor = new
        {
            type = "file",
            fileId = asset.FileId,
            fileName = asset.FileName,
            mediaType = asset.MediaType,
            length = asset.Length
        };
        files.Add(descriptor);
        return descriptor;
    }

    private FileAssetScope RequireScope() =>
        _executionContext.Scope ?? throw new InvalidOperationException(
            "MCP file transfer requires an active file execution scope.");

    private static object CreateResourcePayload(FileAssetContent file) => new
    {
        type = "resource",
        resource = new
        {
            uri = $"openagent://file/{Uri.EscapeDataString(file.Asset.FileId)}",
            mimeType = file.Asset.MediaType,
            blob = Convert.ToBase64String(file.Data)
        }
    };

    private static JsonElement BuildSchema(
        JsonElement schema,
        IReadOnlyList<McpFileTransferBinding> bindings)
    {
        if (bindings.Count == 0 || schema.ValueKind != JsonValueKind.Object)
        {
            return schema.Clone();
        }

        JsonNode? root = JsonNode.Parse(schema.GetRawText());
        if (root is not JsonObject rootObject
            || rootObject["properties"] is not JsonObject properties)
        {
            return schema.Clone();
        }

        foreach (McpFileTransferBinding binding in bindings.Where(
                     item => !string.IsNullOrWhiteSpace(item.ArgumentName)))
        {
            if (properties[binding.ArgumentName] is not JsonObject property)
            {
                continue;
            }

            properties[binding.ArgumentName] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "OpenAgent fileId for the file to send to this MCP tool."
            };
        }

        using JsonDocument document = JsonDocument.Parse(rootObject.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string? ReadString(object? value) => value switch
    {
        string text => text,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
        _ => value?.ToString()
    };

    private static string ResolveFileName(string? uri, string mediaType, int index)
    {
        if (!string.IsNullOrWhiteSpace(uri))
        {
            if (Uri.TryCreate(uri, UriKind.RelativeOrAbsolute, out Uri? parsed))
            {
                string path = parsed.IsAbsoluteUri ? parsed.AbsolutePath : parsed.OriginalString;
                string candidate = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(candidate) && candidate != "/")
                {
                    return candidate;
                }
            }
        }

        string extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
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
