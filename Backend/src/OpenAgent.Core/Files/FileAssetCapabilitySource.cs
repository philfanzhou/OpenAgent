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
                "Read a UTF-8 text file that belongs to the current user or conversation.",
                """{"type":"object","properties":{"fileId":{"type":"string"}},"required":["fileId"]}""",
                AgentResourceType.Tool,
                "file-assets",
                ReadAsync),
            new CapabilityDefinition(
                "write_file",
                "Create a UTF-8 text file for the current user and conversation.",
                """{"type":"object","properties":{"fileName":{"type":"string"},"content":{"type":"string"},"mediaType":{"type":"string"}},"required":["fileName","content"]}""",
                AgentResourceType.Tool,
                "file-assets",
                WriteAsync),
        ];
        if (!string.IsNullOrWhiteSpace(executionContext.Scope.ConversationId))
        {
            definitions.Add(new CapabilityDefinition(
                "download_file",
                "Download a public HTTP(S) file into the current conversation's file storage and return its fileId.",
                """{"type":"object","properties":{"url":{"type":"string","description":"The public HTTP(S) URL of the file to download."}},"required":["url"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "file-assets",
                DownloadAsync));
        }

        return Task.FromResult<IReadOnlyList<CapabilityDefinition>>(definitions.AsReadOnly());
    }

    private async Task<string> ReadAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? fileId = ReadString(arguments, "fileId");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return "文件读取失败：'fileId' 是必填参数，请提供目标文件的 fileId 后重试。";
        }
        if (executionContext.Scope == null)
        {
            return "文件读取失败：文件执行上下文不可用。";
        }
        try
        {
            string content = await files.ReadTextAsync(fileId, executionContext.Scope, cancellationToken).ConfigureAwait(false);
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
            executionContext.RecordCreated(asset);
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
            executionContext.RecordCreated(asset);
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

    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out object? value) ? value?.ToString() : null;
}
