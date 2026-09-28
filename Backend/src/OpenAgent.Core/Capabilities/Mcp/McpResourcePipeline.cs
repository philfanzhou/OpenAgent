using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// MCP 工具结果的资源落盘管道，职责就是"处理 → 上传 → 替换"：结果里的内嵌
/// 二进制资源（<see cref="EmbeddedResourceBlock"/> 包裹的 <see cref="BlobResourceContents"/>）
/// 经 <see cref="IFileAssetService"/> 写入对象存储并登记会话引用，内容块原地替换为
/// <c>[File: ...] fileId=...</c> 描述符——模型不读 base64，拿到内链后用既有的
/// read_file / publish_files / create_file_transfer_url 继续操作。
/// McpClientTool 在结果带 isError/structuredContent/_meta 或含 resource_link 时把
/// 整个 CallToolResult 序列化成 JsonElement 返回；此处反序列化一次、落盘并直接渲染
/// 成文本（规则与 <see cref="ToolResultText"/> 一致），JsonElement 不再外泄。
/// </summary>
internal static class McpResourcePipeline
{
    /// <summary>单次工具结果最多落盘的资源数：防止恶意 server 用海量小 blob 拖垮存储。</summary>
    internal const int MaxPersistedBlobsPerResult = 8;

    /// <summary>
    /// 给 MCP 工具（mcp__ 前缀）套上落盘装饰。位于 IsolatedToolFunction 内层，
    /// 落盘耗时计入单次调用超时；非 AIFunction 的工具原样返回。
    /// </summary>
    internal static AITool Wrap(
        AITool tool,
        IFileAssetService files,
        FileAssetExecutionContext context,
        ILogger? logger = null) =>
        tool is AIFunction function ? new PersistingFunction(function, files, context, logger) : tool;

    private sealed class PersistingFunction(
        AIFunction inner,
        IFileAssetService files,
        FileAssetExecutionContext context,
        ILogger? logger) : AIFunction
    {
        public override string Name => inner.Name;
        public override string Description => inner.Description;
        public override JsonElement JsonSchema => inner.JsonSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            object? result = await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            return await RewriteAsync(result, files, context.Scope, logger, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static async ValueTask<object?> RewriteAsync(
        object? result,
        IFileAssetService files,
        FileAssetScope? scope,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var quota = new PersistQuota();
        switch (result)
        {
            case IEnumerable<AIContent> contents:
            {
                List<AIContent> source = [.. contents];
                List<AIContent>? rewritten = null;
                for (int index = 0; index < source.Count; index++)
                {
                    AIContent? replacement = await TryRewriteContentAsync(
                        source[index], files, scope, logger, quota, cancellationToken).ConfigureAwait(false);
                    if (replacement == null)
                    {
                        continue;
                    }
                    rewritten ??= [.. source];
                    rewritten[index] = replacement;
                }
                return rewritten is null ? result : rewritten;
            }
            case AIContent single:
                return await TryRewriteContentAsync(
                    single, files, scope, logger, quota, cancellationToken).ConfigureAwait(false)
                    ?? result;
            case JsonElement json:
                return await RewriteJsonAsync(json, files, scope, logger, quota, cancellationToken)
                    .ConfigureAwait(false);
            default:
                return result;
        }
    }

    /// <summary>
    /// 上传字节并登记会话引用（read_file / publish_files / create_file_transfer_url
    /// 依赖引用）。失败一律返回 null，由调用方回退为占位符渲染——存储不可用
    /// 不应拖垮整个工具结果。
    /// </summary>
    internal static async ValueTask<FileAsset?> TryStoreAsync(
        IFileAssetService files,
        FileAssetScope? scope,
        string fileName,
        string? mediaType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (scope == null || data.Length == 0)
        {
            return null;
        }

        try
        {
            await using var input = new MemoryStream(data.ToArray(), writable: false);
            FileAsset asset = await files.UploadAsync(
                new FileAssetCreateRequest
                {
                    FileName = fileName,
                    MediaType = mediaType,
                    Source = FileAssetSource.Agent
                },
                input,
                scope,
                cancellationToken).ConfigureAwait(false);
            await files.EnsureReferencesAsync(
                [asset.FileId],
                scope,
                cancellationToken).ConfigureAwait(false);
            return asset;
        }
        catch (Exception exception) when (
            exception is AgentException or InvalidOperationException or NotSupportedException)
        {
            // 存储层拒绝（白名单、超限、功能停用等）：静默降级为占位符。
            return null;
        }
    }

    /// <summary>落盘成功后的模型侧描述符：与用户上传文件的 [File: ...] 约定一致。</summary>
    internal static string Describe(FileAsset asset, string uri) =>
        $"[File: {asset.FileName}] fileId={asset.FileId} ({asset.MediaType}, {asset.Length} bytes) from {uri}";

    /// <summary>
    /// 从资源 URI 推导可存储的文件名：取最后一段路径、去 query、URL 解码、字符消毒；
    /// 无扩展名时按声明的 MIME 经 <see cref="FileMediaTypeCatalog"/> 反查补全（与
    /// 存储层白名单同一事实源），仍无信息则不强行补后缀，由存储层裁决去留。
    /// </summary>
    internal static string DeriveFileName(string uri, string? mimeType)
    {
        string raw = uri;
        int slash = raw.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            raw = raw[(slash + 1)..];
        }
        int query = raw.IndexOf('?');
        if (query >= 0)
        {
            raw = raw[..query];
        }
        raw = Uri.UnescapeDataString(raw);
        var builder = new StringBuilder(raw.Length);
        foreach (char character in raw)
        {
            builder.Append(
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ' ' or '+' or '(' or ')'
                    ? character
                    : '_');
        }
        string name = builder.ToString().Trim();
        // 保留尾部：扩展名在末尾，超长截断优先丢前缀。
        if (name.Length > 80)
        {
            name = name[^80..];
        }
        if (name.Length == 0 || name.All(character => character == '.'))
        {
            name = "mcp-resource";
        }
        if (name.IndexOf('.', 1) < 0
            && !string.IsNullOrEmpty(mimeType)
            && FileMediaTypeCatalog.TryGetExtensionForMediaType(mimeType, out string extension))
        {
            name += extension;
        }
        return name;
    }

    private static async Task<AIContent?> TryRewriteContentAsync(
        AIContent content,
        IFileAssetService files,
        FileAssetScope? scope,
        ILogger? logger,
        PersistQuota quota,
        CancellationToken cancellationToken)
    {
        if (!TryGetBlobResource(content, out string uri, out string? mimeType, out ReadOnlyMemory<byte> data)
            || !quota.TryConsume())
        {
            return null;
        }
        FileAsset? asset = await TryStoreAsync(
            files, scope, DeriveFileName(uri, mimeType), mimeType, data, cancellationToken)
            .ConfigureAwait(false);
        if (asset == null)
        {
            logger?.LogDebug("MCP blob resource not persisted; keeping placeholder. Uri={Uri}", uri);
            return null;
        }
        return new TextContent(Describe(asset, uri));
    }

    private static bool TryGetBlobResource(
        AIContent content,
        out string uri,
        out string? mimeType,
        out ReadOnlyMemory<byte> data)
    {
        uri = string.Empty;
        mimeType = null;
        data = default;
        // 内嵌二进制资源投影成 DataContent（字节在 Data 上，URI 只在 RawRepresentation
        // 里）；普通 image/audio 块没有资源 URI，不落盘。
        if (content is not DataContent { Data.Length: > 0 } block
            || block.RawRepresentation is not EmbeddedResourceBlock { Resource: BlobResourceContents blob }
            || string.IsNullOrEmpty(blob.Uri))
        {
            return false;
        }
        uri = blob.Uri;
        mimeType = blob.MimeType;
        data = block.Data;
        return true;
    }

    private static async ValueTask<object?> RewriteJsonAsync(
        JsonElement json,
        IFileAssetService files,
        FileAssetScope? scope,
        ILogger? logger,
        PersistQuota quota,
        CancellationToken cancellationToken)
    {
        if (!LooksLikeCallToolResult(json))
        {
            return json;
        }
        CallToolResult? result;
        try
        {
            result = JsonSerializer.Deserialize(
                json,
                typeof(CallToolResult),
                McpJsonUtilities.DefaultOptions) as CallToolResult;
        }
        catch (JsonException)
        {
            // 形似 CallToolResult 但结构损坏：原样放行，不吞数据。
            return json;
        }
        if (result == null)
        {
            return json;
        }

        await TryRewriteBlocksAsync(result, files, scope, logger, quota, cancellationToken)
            .ConfigureAwait(false);
        return ToolResultText.RenderCallToolResult(result);
    }

    // JsonElement 形态由 McpClientTool 兜底产生，但任意工具都可能返回 JSON：
    // 形似 CallToolResult（三特征属性居一）才解读，其余原样放行。
    private static bool LooksLikeCallToolResult(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object
        && ((json.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            || json.TryGetProperty("structuredContent", out _)
            || json.TryGetProperty("isError", out _));

    private static async Task TryRewriteBlocksAsync(
        CallToolResult result,
        IFileAssetService files,
        FileAssetScope? scope,
        ILogger? logger,
        PersistQuota quota,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < result.Content.Count; index++)
        {
            if (result.Content[index] is not EmbeddedResourceBlock { Resource: BlobResourceContents blob }
                || blob.DecodedData.Length == 0
                || string.IsNullOrEmpty(blob.Uri)
                || !quota.TryConsume())
            {
                continue;
            }
            FileAsset? asset = await TryStoreAsync(
                files,
                scope,
                DeriveFileName(blob.Uri, blob.MimeType),
                blob.MimeType,
                blob.DecodedData,
                cancellationToken).ConfigureAwait(false);
            if (asset == null)
            {
                logger?.LogDebug("MCP blob resource not persisted; keeping placeholder. Uri={Uri}", blob.Uri);
                continue;
            }
            result.Content[index] = new TextContentBlock { Text = Describe(asset, blob.Uri) };
        }
    }

    /// <summary>单次结果内的落盘名额（顺序消费）：达到上限后不再尝试。</summary>
    private sealed class PersistQuota
    {
        private int consumed;

        public bool TryConsume() => ++consumed <= MaxPersistedBlobsPerResult;
    }
}
