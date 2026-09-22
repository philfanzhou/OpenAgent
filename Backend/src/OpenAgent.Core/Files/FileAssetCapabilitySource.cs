using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetCapabilitySource(
    IFileAssetService files,
    IFileShareService shares,
    FileAssetExecutionContext executionContext,
    IOptions<FileAssetOptions> options,
    FileAssetUrlDownloader downloader) : ICapabilitySource
{
    // 错误信封约定：code 稳定供程序判别，error/hint 面向模型给出可行动的修正路径。
    private const string InvalidArguments = "invalid_arguments";
    private const string UnavailableContext = "unavailable_context";
    private const string InvalidRequest = "invalid_request";
    private const string NotFound = "not_found";
    private const string DownloadFailed = "download_failed";

    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || executionContext.Scope == null)
        {
            return Task.FromResult<IReadOnlyList<CapabilityDefinition>>([]);
        }

        List<CapabilityDefinition> definitions =
        [
            new CapabilityDefinition(
                "read_file",
                "Read a UTF-8 text file by fileId or by an objectKey inside the current tenant partition — provide exactly one of the two, never both. "
                + "Call list_files first when you do not know the fileId. "
                + "Text files only (.txt, .md, .csv, .json, .xml, .svg, .html, .css, .drawio and other UTF-8 text); "
                + "binary files such as images, PDF, zip, or office documents cannot be read as text — "
                + "deliver those to the user via publish_files or create_file_transfer_url instead. "
                + "The result is JSON {fileId|objectKey, content}; oversized results are truncated with a marker, "
                + "so ask for specific sections instead of re-reading the whole file.",
                """{"type":"object","properties":{"fileId":{"type":"string","description":"ID of the file asset to read; exactly one of fileId/objectKey"},"objectKey":{"type":"string","description":"Object key inside the current tenant partition; exactly one of fileId/objectKey"}},"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                ReadAsync,
                Concurrency: ToolConcurrency.ReadOnly),
            new CapabilityDefinition(
                "create_file_transfer_url",
                "Create a platform-served download link for a file (the storage address is never exposed). "
                + "audience=\"mcp\" hands the URL to an external MCP tool (2h validity, 2 downloads); "
                + "audience=\"user\" gives the user a download link (3 days, unlimited). "
                + "Optional mode temporary|singleUse|longTerm or expiresInSeconds override the defaults; "
                + "lifetimes cap at 365 days and permanent links do not exist. "
                + "Always tell the user the expiry and download limit. "
                + "You cannot revoke a link once created, so prefer the shortest lifetime that suffices.",
                """{"type":"object","properties":{"fileId":{"type":"string","description":"Referenced file asset ID"},"audience":{"type":"string","enum":["mcp","user"],"description":"Consumer of the link; sets defaults when mode is omitted (mcp: 2h/2 downloads, user: 3d/unlimited)"},"mode":{"type":"string","enum":["temporary","singleUse","longTerm"],"description":"Share policy overriding audience defaults"},"expiresInSeconds":{"type":"number","description":"Custom lifetime in seconds; must be a positive integer"}},"required":["fileId"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                CreateShareLinkAsync),
            new CapabilityDefinition(
                "list_files",
                "List files referenced by the current conversation; returns fileId and safe metadata only. "
                + "Call this before read_file/publish_files when unsure which files exist. "
                + "Use read_file to inspect one, publish_files to deliver files to the user.",
                """{"type":"object","properties":{},"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                ListAsync,
                Concurrency: ToolConcurrency.ReadOnly),
            new CapabilityDefinition(
                "write_file",
                "Create and register a NEW UTF-8 text file for the current user and conversation; returns its fileId for use with publish_files. "
                + "This tool cannot modify, append to, or overwrite an existing file — to produce a revised version, call it again with the complete new content (every call creates a new file). "
                + "Send the full final content in one call; there is no partial update.",
                """{"type":"object","properties":{"fileName":{"type":"string","description":"Target file name, e.g. report.txt or circuit.drawio"},"content":{"type":"string","description":"Complete UTF-8 text content of the file"},"mediaType":{"type":"string","description":"Optional MIME type; inferred from the fileName extension when omitted — omit it unless you have a specific reason"}},"required":["fileName","content"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                WriteAsync),
            new CapabilityDefinition(
                "compress_files",
                "Zip files into one archive, register it as a file asset and return its fileId. "
                + "Each item targets a fileId, or an objectKey with fileName. "
                + "Publish the archive with publish_files to deliver it.",
                """{"type":"object","properties":{"outputName":{"type":"string","description":"Output zip name, e.g. report.zip"},"items":{"type":"array","minItems":1,"items":{"type":"object","properties":{"fileId":{"type":"string","description":"Referenced file asset ID"},"objectKey":{"type":"string","description":"Object key inside the current tenant partition (pair with fileName)"},"fileName":{"type":"string","description":"Name recorded in the archive for an objectKey item"}},"additionalProperties":false}}},"required":["outputName","items"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                CompressAsync),
            new CapabilityDefinition(
                "publish_files",
                "Attach existing file assets (by fileId, from write_file/compress_files/earlier operations) to this assistant message for user download or preview. No bytes are copied. "
                + "This is the only way to hand files to the user — content written by tools alone is invisible to them.",
                """{"type":"object","properties":{"fileIds":{"type":"array","items":{"type":"string"},"description":"Existing file asset IDs to deliver"}},"required":["fileIds"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                PublishAsync)
        ];
        if (!string.IsNullOrWhiteSpace(executionContext.Scope.ConversationId))
        {
            definitions.Add(new CapabilityDefinition(
                "download_file",
                "Download a public HTTP(S) file into the conversation's file storage; returns its fileId. "
                + "Only public direct file URLs are supported — this is not a general web fetch.",
                """{"type":"object","properties":{"url":{"type":"string","description":"The public HTTP(S) URL of the file to download."}},"required":["url"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                DownloadAsync));
        }
        return Task.FromResult<IReadOnlyList<CapabilityDefinition>>(definitions);
    }

    private async Task<ToolResult> ReadAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? fileId = ReadString(arguments, "fileId");
        string? objectKey = ReadString(arguments, "objectKey");
        if (string.IsNullOrWhiteSpace(fileId) == string.IsNullOrWhiteSpace(objectKey))
        {
            return ToolResult.Error(
                "Provide exactly one of 'fileId' or 'objectKey' — not both, not neither.",
                InvalidArguments,
                hint: "Call list_files to discover fileIds.");
        }
        if (executionContext.Scope == null)
        {
            return ToolResult.Error(
                "The file execution context is unavailable for this request.",
                UnavailableContext);
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(objectKey))
            {
                string objectContent = await files.ReadObjectTextAsync(objectKey, executionContext.Scope, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { objectKey, content = objectContent });
            }
            string content = await files.ReadTextAsync(fileId!, executionContext.Scope, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { fileId, content });
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            // 返回净化后的校验错误文本，供模型修正后重试，不把原始异常泄露给模型。
            return ToolResult.Error(exception.Message, InvalidRequest);
        }
    }

    private async Task<ToolResult> ListAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (executionContext.Scope == null)
        {
            return ToolResult.Error(
                "The file execution context is unavailable for this request.",
                UnavailableContext);
        }

        try
        {
            IReadOnlyList<FileAsset> assets = await files.ListAsync(
                executionContext.Scope,
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                files = assets.Select(asset => new
                {
                    fileId = asset.FileId,
                    fileName = asset.FileName,
                    mediaType = asset.MediaType,
                    length = asset.Length,
                    source = asset.Source.ToString(),
                    state = asset.State.ToString(),
                    createdAt = asset.CreatedAt
                }).ToArray()
            });
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            return ToolResult.Error(exception.Message, InvalidRequest);
        }
    }

    private async Task<ToolResult> CreateShareLinkAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? fileId = ReadString(arguments, "fileId");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return ToolResult.Error(
                "'fileId' is a required argument.",
                InvalidArguments,
                hint: "Call list_files to discover fileIds.");
        }
        if (executionContext.Scope == null)
        {
            return ToolResult.Error(
                "The file execution context is unavailable for this request.",
                UnavailableContext);
        }
        string? modeRaw = ReadString(arguments, "mode");
        if (modeRaw != null && !FileShareModeParser.TryParse(modeRaw, out _))
        {
            return ToolResult.Error(
                $"'mode' does not allow the value '{modeRaw}'.",
                InvalidArguments,
                hint: "Allowed values: temporary, singleUse, longTerm.");
        }
        string? audienceRaw = ReadString(arguments, "audience");
        if (audienceRaw != null && !FileShareAudienceParser.TryParse(audienceRaw, out _))
        {
            return ToolResult.Error(
                $"'audience' does not allow the value '{audienceRaw}'.",
                InvalidArguments,
                hint: "Allowed values: mcp, user.");
        }
        int? expiresInSeconds = ReadInt32(arguments, "expiresInSeconds");
        if (expiresInSeconds is < 1)
        {
            return ToolResult.Error(
                "'expiresInSeconds' must be a positive integer (seconds).",
                InvalidArguments);
        }

        try
        {
            FileAsset? asset = await files.GetReferencedAsync(
                fileId,
                executionContext.Scope,
                cancellationToken).ConfigureAwait(false);
            if (asset == null || asset.State != FileAssetState.Ready)
            {
                return ToolResult.Error(
                    "The file does not exist, is not ready, or is not referenced by the current conversation.",
                    NotFound,
                    hint: "Call list_files to check which files are available.");
            }

            FileShareMode? mode = null;
            if (modeRaw != null && FileShareModeParser.TryParse(modeRaw, out FileShareMode parsedMode))
            {
                mode = parsedMode;
            }
            FileShareAudience? audience = null;
            if (audienceRaw != null && FileShareAudienceParser.TryParse(audienceRaw, out FileShareAudience parsedAudience))
            {
                audience = parsedAudience;
            }

            FileShareLink share = await shares.CreateAsync(
                fileId,
                executionContext.Scope,
                new FileShareRequest { Mode = mode, Audience = audience, ExpiresInSeconds = expiresInSeconds },
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                fileId,
                shareId = share.ShareId,
                url = share.Url,
                mode = share.Mode.ToString(),
                expiresAt = share.ExpiresAt,
                maxDownloads = share.MaxDownloads
            });
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            return ToolResult.Error(exception.Message, InvalidRequest);
        }
    }

    private static int? ReadInt32(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        string? value = ReadString(arguments, name);
        return value != null && int.TryParse(value, out int parsed) ? parsed : null;
    }

    private async Task<ToolResult> WriteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? fileName = ReadString(arguments, "fileName");
        string? content = ReadString(arguments, "content");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return ToolResult.Error(
                "'fileName' is a required argument; provide the target file name (e.g. report.txt or circuit.drawio).",
                InvalidArguments);
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return ToolResult.Error(
                "'content' is a required argument; provide the full file content.",
                InvalidArguments);
        }
        // 不默认 text/plain：伪造的具体类型会与扩展名一致性校验冲突（如 .json + text/plain 被拒）。
        // 留空让服务端按扩展名推断规范化类型。
        string? mediaType = ReadString(arguments, "mediaType");
        if (executionContext.Scope == null)
        {
            return ToolResult.Error(
                "The file execution context is unavailable for this request.",
                UnavailableContext);
        }
        try
        {
            byte[] data = new UTF8Encoding(false).GetBytes(content);
            await using var input = new MemoryStream(data, writable: false);
            FileAsset asset = await files.UploadAsync(
                new FileAssetCreateRequest
                {
                    FileName = fileName,
                    MediaType = mediaType,
                    Source = FileAssetSource.Agent
                },
                input,
                executionContext.Scope,
                cancellationToken).ConfigureAwait(false);
            await files.EnsureReferencesAsync([asset.FileId], executionContext.Scope, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                fileId = asset.FileId,
                fileName = asset.FileName,
                mediaType = asset.MediaType,
                length = asset.Length
            });
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            // 类型/大小等校验失败：返回净化后的错误文本，供模型修正后重试，不把原始异常泄露给模型。
            return ToolResult.Error(exception.Message, InvalidRequest);
        }
    }

    private async Task<ToolResult> DownloadAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? url = ReadString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return ToolResult.Error(
                "'url' is a required argument; provide the public HTTP(S) address of the file.",
                InvalidArguments);
        }
        FileAssetScope? scope = executionContext.Scope;
        if (scope == null || string.IsNullOrWhiteSpace(scope.ConversationId))
        {
            return ToolResult.Error(
                "This request has no conversation to bind the downloaded file to.",
                UnavailableContext);
        }

        try
        {
            DownloadedFile downloaded = await downloader.DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            await using var input = new MemoryStream(downloaded.Content, writable: false);
            FileAsset asset = await files.UploadAsync(
                new FileAssetCreateRequest
                {
                    FileName = downloaded.FileName,
                    MediaType = downloaded.MediaType,
                    Source = FileAssetSource.Agent
                },
                input,
                scope,
                cancellationToken).ConfigureAwait(false);
            await files.EnsureReferencesAsync([asset.FileId], scope, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                fileId = asset.FileId,
                fileName = asset.FileName,
                mediaType = asset.MediaType,
                length = asset.Length,
                source = "download"
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            return ToolResult.Error(exception.Message, InvalidRequest);
        }
        catch (HttpRequestException)
        {
            return ToolResult.Error(
                "The remote address is unreachable.",
                DownloadFailed,
                hint: "Verify the URL is public and reachable, then retry.");
        }
        catch (TaskCanceledException)
        {
            return ToolResult.Error(
                "The remote address timed out.",
                DownloadFailed,
                hint: "Retry, or point at a faster mirror of the file.");
        }
    }

    private async Task<ToolResult> CompressAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? outputName = ReadString(arguments, "outputName");
        if (string.IsNullOrWhiteSpace(outputName))
        {
            return ToolResult.Error(
                "'outputName' is a required argument; provide the output zip name (e.g. report.zip).",
                InvalidArguments);
        }
        if (!arguments.TryGetValue("items", out object? itemsValue) || itemsValue == null)
        {
            return ToolResult.Error(
                "'items' is a required argument; provide at least one file to pack (fileId, or objectKey+fileName).",
                InvalidArguments);
        }
        IReadOnlyList<FileArchiveItem> items;
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(itemsValue);
            items = JsonSerializer.Deserialize<IReadOnlyList<FileArchiveItem>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException)
        {
            return ToolResult.Error(
                "'items' is not a valid array of pack entries.",
                InvalidArguments,
                hint: "Use [{\"fileId\":\"...\"}] or [{\"objectKey\":\"...\",\"fileName\":\"...\"}].");
        }
        if (items.Count == 0)
        {
            return ToolResult.Error(
                "'items' must contain at least one file to pack.",
                InvalidArguments);
        }
        if (executionContext.Scope == null)
        {
            return ToolResult.Error(
                "The file execution context is unavailable for this request.",
                UnavailableContext);
        }
        try
        {
            FileArchiveResult result = await files.CompressAsync(
                new FileArchiveRequest
                {
                    OutputName = outputName,
                    Items = items
                },
                executionContext.Scope,
                cancellationToken).ConfigureAwait(false);
            await files.EnsureReferencesAsync(
                [result.Asset.FileId],
                executionContext.Scope,
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                fileId = result.Asset.FileId,
                fileName = result.Asset.FileName,
                mediaType = result.Asset.MediaType,
                objectKey = result.Asset.ObjectKey,
                length = result.Asset.Length,
                fileCount = result.FileCount
            });
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            // 返回净化后的错误文本，供模型修正后重试，不把原始异常泄露给模型。
            return ToolResult.Error(exception.Message, InvalidRequest);
        }
    }

    private async Task<ToolResult> PublishAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> fileIds = ReadStrings(arguments, "fileIds");
        if (fileIds.Count == 0)
        {
            return ToolResult.Error(
                "'fileIds' is a required argument; provide at least one file ID.",
                InvalidArguments,
                hint: "Call list_files to discover fileIds.");
        }
        if (executionContext.Scope == null)
        {
            return ToolResult.Error(
                "The file execution context is unavailable for this request.",
                UnavailableContext);
        }

        try
        {
            List<FileAsset> assets = [];
            foreach (string fileId in fileIds)
            {
                FileAsset? asset = await files.GetAsync(
                    fileId,
                    executionContext.Scope,
                    cancellationToken).ConfigureAwait(false);
                if (asset == null || asset.State != FileAssetState.Ready)
                {
                    // 不回显调用方传入的 fileId：保持与旧契约一致的净化口径。
                    return ToolResult.Error(
                        "One or more files do not exist, are not ready, or do not belong to the current user.",
                        NotFound,
                        hint: "Call list_files to check which files are available.");
                }
                assets.Add(asset);
            }

            await files.EnsureReferencesAsync(
                fileIds,
                executionContext.Scope,
                cancellationToken).ConfigureAwait(false);
            foreach (FileAsset asset in assets)
            {
                executionContext.RecordPublished(asset);
            }

            return JsonSerializer.Serialize(new
            {
                files = assets.Select(asset => new
                {
                    fileId = asset.FileId,
                    fileName = asset.FileName,
                    mediaType = asset.MediaType,
                    objectKey = asset.ObjectKey,
                    length = asset.Length
                }).ToArray()
            });
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            return ToolResult.Error(exception.Message, InvalidRequest);
        }
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out object? value) ? value?.ToString() : null;

    private static IReadOnlyList<string> ReadStrings(
        IReadOnlyDictionary<string, object?> arguments,
        string name)
    {
        if (!arguments.TryGetValue(name, out object? value) || value == null)
        {
            return [];
        }

        IEnumerable<string?> values = value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.Array =>
                element.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.String ? item.GetString() : null),
            IEnumerable<string> strings => strings,
            IEnumerable<object?> objects => objects.Select(item => item?.ToString()),
            _ => []
        };

        return values
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
