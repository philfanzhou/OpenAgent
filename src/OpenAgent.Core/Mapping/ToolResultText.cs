using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Mapping;

/// <summary>
/// 把工具结果展平为可读文本。工具结果会被持久化进会话历史并回喂模型，直接
/// ToString() 只剩类型名（"Microsoft.Extensions.AI.AIContent[]"），模型看到
/// 无意义输出后会反复空参重试同一工具。MCP 内容块规则：text 块取正文；其余类型
/// （图片/音频/二进制资源/资源链接）保留 URI、数据只留占位符——base64 全文
/// 对模型不可读且会撑爆上下文。
/// </summary>
internal static class ToolResultText
{
    internal static string? Render(object? result) => result switch
    {
        null => null,
        string text => text,
        ToolResult toolResult => toolResult.Content,
        IEnumerable<AIContent> contents => JoinContents(contents),
        AIContent content => RenderContent(content),
        JsonElement json => json.ValueKind == JsonValueKind.Undefined ? null : json.GetRawText(),
        _ => RenderUnknown(result)
    };

    /// <summary>展平 AIContent 集合（MCP 多内容块返回值）；各部分以换行连接。</summary>
    internal static string JoinContents(IEnumerable<AIContent> contents) =>
        string.Join("\n", contents.Select(RenderContent).Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>
    /// 展平 CallToolResult（McpClientTool 对带 isError/structuredContent/_meta 或
    /// resource_link 的结果整体序列化成 JsonElement，由 MCP 落盘管道反序列化后
    /// 经由此处渲染）。_meta 属协议层管道信息，不参与渲染；isError 与
    /// ErrorContent 一致地加前缀，模型才能与正常结果区分开。
    /// </summary>
    internal static string RenderCallToolResult(CallToolResult result)
    {
        var parts = new List<string>();
        foreach (ContentBlock block in result.Content)
        {
            string part = RenderBlock(block);
            if (!string.IsNullOrEmpty(part))
            {
                parts.Add(part);
            }
        }

        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured)
        {
            parts.Add(structured.GetRawText());
        }

        string joined = string.Join("\n", parts);
        return result.IsError == true && joined.Length > 0 ? $"[tool error] {joined}" : joined;
    }

    private static string RenderContent(AIContent content) => content switch
    {
        TextContent text => text.Text ?? string.Empty,
        ErrorContent error => string.IsNullOrWhiteSpace(error.Message)
            ? "[tool error]"
            : $"[tool error] {error.Message}",
        DataContent data => RenderDataContent(data),
        UriContent { Uri: { } uri } => uri.ToString(),
        _ => string.Empty
    };

    private static string RenderBlock(ContentBlock block) => block switch
    {
        TextContentBlock text => text.Text ?? string.Empty,
        EmbeddedResourceBlock { Resource: TextResourceContents textResource } => textResource.Text ?? string.Empty,
        EmbeddedResourceBlock { Resource: BlobResourceContents blob } => RenderBinary(
            blob.Uri,
            blob.MimeType ?? "application/octet-stream",
            blob.DecodedData.Length),
        ImageContentBlock image => RenderBinary(null, image.MimeType, image.DecodedData.Length),
        AudioContentBlock audio => RenderBinary(null, audio.MimeType, audio.DecodedData.Length),
        ResourceLinkBlock link => link.Uri,
        _ => string.Empty
    };

    /// <summary>
    /// 内嵌二进制资源投影成 DataContent 后，资源 URI 只存在于 RawRepresentation
    /// （SDK 用块级 _meta 覆盖 AdditionalProperties，uri 项不保）：必须从这里带出来，
    /// 模型才有线索凭 URI 再去获取该资源。
    /// </summary>
    private static string RenderDataContent(DataContent data)
    {
        string? uri = data.Uri is { } dataUri && !dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? dataUri
            : (data.RawRepresentation as EmbeddedResourceBlock)?.Resource.Uri;
        if (data.Data.Length == 0)
        {
            return uri ?? string.Empty;
        }

        return uri is null
            ? $"[binary content: {data.MediaType}, {data.Data.Length} bytes]"
            : $"{uri} [binary content: {data.MediaType}, {data.Data.Length} bytes]";
    }

    private static string RenderBinary(string? uri, string mediaType, int byteCount)
    {
        string placeholder = $"[binary content: {mediaType}, {byteCount} bytes]";
        return uri is null ? placeholder : $"{uri} {placeholder}";
    }

    private static string RenderUnknown(object result)
    {
        // 其余类型（原始值、字典、匿名对象）优先 JSON 序列化：无损且模型可读；
        // 不可序列化对象退化为 ToString（与旧行为一致）。
        try
        {
            return JsonSerializer.Serialize(result);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException
            || exception is InvalidOperationException)
        {
            return result.ToString() ?? string.Empty;
        }
    }
}
