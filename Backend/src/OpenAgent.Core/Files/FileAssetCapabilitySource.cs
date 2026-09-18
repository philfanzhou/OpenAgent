using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
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
                "Read a UTF-8 text file owned by the current user or conversation, by fileId or by an objectKey inside the current tenant partition.",
                """{"type":"object","properties":{"fileId":{"type":"string"},"objectKey":{"type":"string"}}}""",
                AgentResourceType.Tool,
                "file-assets",
                ReadAsync),
            new CapabilityDefinition(
                "create_file_transfer_url",
                "Create a platform-served download link for a file (the storage address is never exposed). "
                + "audience=\"mcp\" hands the URL to an external MCP tool (2h validity, 2 downloads); "
                + "audience=\"user\" gives the user a download link (3 days, unlimited). "
                + "Optional mode temporary|singleUse|longTerm or expiresInSeconds override the defaults; "
                + "lifetimes cap at 365 days and permanent links do not exist. "
                + "Always tell the user the expiry and download limit. "
                + "You cannot revoke a link once created, so prefer the shortest lifetime that suffices.",
                """{"type":"object","properties":{"fileId":{"type":"string","description":"Referenced file asset ID"},"audience":{"type":"string","enum":["mcp","user"],"description":"Consumer of the link; sets defaults when mode is omitted (mcp: 2h/2 downloads, user: 3d/unlimited)"},"mode":{"type":"string","enum":["temporary","singleUse","longTerm"],"description":"Share policy overriding audience defaults"},"expiresInSeconds":{"type":"number","description":"Custom lifetime in seconds"}},"required":["fileId"]}""",
                AgentResourceType.Tool,
                "file-assets",
                CreateShareLinkAsync),
            new CapabilityDefinition(
                "list_files",
                "List files referenced by the current conversation; returns fileId and safe metadata only. "
                + "Use read_file to inspect one, publish_files to deliver files to the user.",
                """{"type":"object","properties":{}}""",
                AgentResourceType.Tool,
                "file-assets",
                ListAsync),
            new CapabilityDefinition(
                "write_file",
                "Create and register a UTF-8 text file for the current user and conversation; returns its fileId for use with publish_files.",
                """{"type":"object","properties":{"fileName":{"type":"string"},"content":{"type":"string"},"mediaType":{"type":"string"}},"required":["fileName","content"]}""",
                AgentResourceType.Tool,
                "file-assets",
                WriteAsync),
            new CapabilityDefinition(
                "compress_files",
                "Zip files into one archive, register it as a file asset and return its fileId. "
                + "Each item targets a fileId, or an objectKey with fileName. Publish the archive with publish_files to deliver it.",
                """{"type":"object","properties":{"outputName":{"type":"string","description":"zip name, e.g. report.zip"},"items":{"type":"array","items":{"type":"object","properties":{"fileId":{"type":"string"},"objectKey":{"type":"string"},"fileName":{"type":"string"}}}}},"required":["outputName","items"]}""",
                AgentResourceType.Tool,
                "file-assets",
                CompressAsync),
            new CapabilityDefinition(
                "publish_files",
                "Attach existing file assets (by fileId, from write_file/compress_files/earlier operations) to this assistant message for user download or preview. No bytes are copied.",
                """{"type":"object","properties":{"fileIds":{"type":"array","items":{"type":"string"},"description":"Existing file asset IDs to deliver"}},"required":["fileIds"]}""",
                AgentResourceType.Tool,
                "file-assets",
                PublishAsync)
        ];
        if (!string.IsNullOrWhiteSpace(executionContext.Scope.ConversationId))
        {
            definitions.Add(new CapabilityDefinition(
                "download_file",
                "Download a public HTTP(S) file into the conversation's file storage; returns its fileId.",
                """{"type":"object","properties":{"url":{"type":"string","description":"The public HTTP(S) URL of the file to download."}},"required":["url"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                DownloadAsync));
        }
        return Task.FromResult<IReadOnlyList<CapabilityDefinition>>(definitions);
    }

    private async Task<string> ReadAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? fileId = ReadString(arguments, "fileId");
        string? objectKey = ReadString(arguments, "objectKey");
        if (string.IsNullOrWhiteSpace(fileId) == string.IsNullOrWhiteSpace(objectKey))
        {
            return "文件读取失败：请提供 'fileId' 或 'objectKey' 之一（不可同时提供或同时缺失）。";
        }
        if (executionContext.Scope == null)
        {
            return "文件读取失败：文件执行上下文不可用。";
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
            return $"文件读取失败：{exception.Message}";
        }
    }

    private async Task<string> ListAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (executionContext.Scope == null)
        {
            return "文件列表获取失败：文件执行上下文不可用。";
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
            return $"文件列表获取失败：{exception.Message}";
        }
    }

    private async Task<string> CreateShareLinkAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? fileId = ReadString(arguments, "fileId");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return "文件分享链接生成失败：'fileId' 是必填参数。";
        }
        if (executionContext.Scope == null)
        {
            return "文件分享链接生成失败：文件执行上下文不可用。";
        }
        string? modeRaw = ReadString(arguments, "mode");
        if (modeRaw != null && !FileShareModeParser.TryParse(modeRaw, out _))
        {
            return "文件分享链接生成失败：'mode' 只支持 temporary、singleUse 或 longTerm。";
        }
        string? audienceRaw = ReadString(arguments, "audience");
        if (audienceRaw != null && !FileShareAudienceParser.TryParse(audienceRaw, out _))
        {
            return "文件分享链接生成失败：'audience' 只支持 mcp 或 user。";
        }
        int? expiresInSeconds = ReadInt32(arguments, "expiresInSeconds");
        if (expiresInSeconds is < 1)
        {
            return "文件分享链接生成失败：'expiresInSeconds' 必须是正整数（秒）。";
        }

        try
        {
            FileAsset? asset = await files.GetReferencedAsync(
                fileId,
                executionContext.Scope,
                cancellationToken).ConfigureAwait(false);
            if (asset == null || asset.State != FileAssetState.Ready)
            {
                return "文件分享链接生成失败：文件不存在、未就绪或未关联到当前会话。";
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
            return $"文件分享链接生成失败：{exception.Message}";
        }
    }

    private static int? ReadInt32(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        string? value = ReadString(arguments, name);
        return value != null && int.TryParse(value, out int parsed) ? parsed : null;
    }

    private async Task<string> WriteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? fileName = ReadString(arguments, "fileName");
        string? content = ReadString(arguments, "content");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "文件写入失败：'fileName' 是必填参数，请提供目标文件名（如 report.txt 或 circuit.drawio）后重试。";
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return "文件写入失败：'content' 是必填参数，请提供文件内容后重试。";
        }
        string mediaType = ReadString(arguments, "mediaType") ?? "text/plain";
        if (executionContext.Scope == null)
        {
            return "文件写入失败：文件执行上下文不可用。";
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
            return $"文件写入失败：{exception.Message}";
        }
    }

    private async Task<string> DownloadAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? url = ReadString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return "文件下载失败：'url' 是必填参数，请提供公开的 HTTP(S) 文件地址后重试。";
        }
        FileAssetScope? scope = executionContext.Scope;
        if (scope == null || string.IsNullOrWhiteSpace(scope.ConversationId))
        {
            return "文件下载失败：当前请求没有可绑定的会话。";
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
            return $"文件下载失败：{exception.Message}";
        }
        catch (HttpRequestException)
        {
            return "文件下载失败：远程地址不可访问。";
        }
        catch (TaskCanceledException)
        {
            return "文件下载失败：远程地址响应超时。";
        }
    }

    private async Task<string> CompressAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? outputName = ReadString(arguments, "outputName");
        if (string.IsNullOrWhiteSpace(outputName))
        {
            return "文件压缩失败：'outputName' 是必填参数，请提供输出 zip 文件名（如 report.zip）后重试。";
        }
        if (!arguments.TryGetValue("items", out object? itemsValue) || itemsValue == null)
        {
            return "文件压缩失败：'items' 是必填参数，请提供至少一个待打包文件（fileId 或 objectKey+fileName）。";
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
            return "文件压缩失败：'items' 格式无效，请按 [{\"fileId\":\"...\"}] 或 [{\"objectKey\":\"...\",\"fileName\":\"...\"}] 提供。";
        }
        if (items.Count == 0)
        {
            return "文件压缩失败：'items' 至少需要一个待打包文件。";
        }
        if (executionContext.Scope == null)
        {
            return "文件压缩失败：文件执行上下文不可用。";
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
            // 返回净化后的校验错误文本，供模型修正后重试，不把原始异常泄露给模型。
            return $"文件压缩失败：{exception.Message}";
        }
    }

    private async Task<string> PublishAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> fileIds = ReadStrings(arguments, "fileIds");
        if (fileIds.Count == 0)
        {
            return "文件发布失败：'fileIds' 是必填参数，请提供至少一个文件 ID。";
        }
        if (executionContext.Scope == null)
        {
            return "文件发布失败：文件执行上下文不可用。";
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
                    return "文件发布失败：文件不存在、未就绪或不属于当前用户。";
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
            return $"文件发布失败：{exception.Message}";
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
