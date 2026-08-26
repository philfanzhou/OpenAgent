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
    FileAssetExecutionContext executionContext,
    IOptions<FileAssetOptions> options) : ICapabilitySource
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

        IReadOnlyList<CapabilityDefinition> definitions =
        [
            new CapabilityDefinition(
                "read_file",
                "Read a UTF-8 text file that belongs to the current user or conversation, "
                + "either by fileId or by an object storage key inside the current tenant partition.",
                """{"type":"object","properties":{"fileId":{"type":"string"},"objectKey":{"type":"string"}}}""",
                AgentResourceType.Tool,
                "file-assets",
                ReadAsync),
            new CapabilityDefinition(
                "write_file",
                "Create and register a UTF-8 text file for the current user and conversation. "
                + "The returned fileId can be passed to publish_files when it should be delivered to the user.",
                """{"type":"object","properties":{"fileName":{"type":"string"},"content":{"type":"string"},"mediaType":{"type":"string"}},"required":["fileName","content"]}""",
                AgentResourceType.Tool,
                "file-assets",
                WriteAsync),
            new CapabilityDefinition(
                "compress_files",
                "Compress files into one zip archive, register it as a downloadable file asset, and return its fileId. "
                + "The archive is not added to the assistant message until publish_files is called. "
                + "Each item targets a file by fileId (conversation-referenced) or by objectKey with fileName. "
                + "Returns the fileId, objectKey, length, and file count.",
                """{"type":"object","properties":{"outputName":{"type":"string","description":"zip file name, e.g. report.zip"},"items":{"type":"array","items":{"type":"object","properties":{"fileId":{"type":"string"},"objectKey":{"type":"string"},"fileName":{"type":"string"}}}}},"required":["outputName","items"]}""",
                AgentResourceType.Tool,
                "file-assets",
                CompressAsync),
            new CapabilityDefinition(
                "publish_files",
                "Publish one or more existing file assets to the current assistant message for user download or preview. "
                + "Use fileIds returned by write_file, compress_files, or earlier file operations. "
                + "Publishing does not copy file bytes; it only associates the selected assets with this message.",
                """{"type":"object","properties":{"fileIds":{"type":"array","items":{"type":"string"},"description":"Existing file asset IDs to deliver to the user"}},"required":["fileIds"]}""",
                AgentResourceType.Tool,
                "file-assets",
                PublishAsync)
        ];
        return Task.FromResult(definitions);
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
