using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 把工具结果安全渲染为文本。MCP 工具返回多个内容块时结果形如
/// <see cref="AIContent"/> 数组，直接 <c>ToString()</c> 只会得到类型名
/// （"Microsoft.Extensions.AI.AIContent[]"）：该字符串既被持久化进会话历史，
/// 又原样回喂给模型，模型看到无意义输出后会反复空参重试同一工具。
/// 所有展示、持久化与回喂模型的工具结果都应经由此处展平为可读文本。
/// MCP 内容块的展平规则：text 块取正文；其余类型（图片/音频/二进制资源/资源链接）
/// 保留 URI，数据本身只留占位符——base64 全文对模型不可读且会撑爆上下文。
/// </summary>
internal static class ToolResultText
{
    internal static string? Render(object? result) => result switch
    {
        null => null,
        string text => text,
        ToolResult toolResult => toolResult.Content,
        IEnumerable<AIContent> contents => JoinContents(contents),
        // 单个 AIContent（MCP 单内容块结果）：与集合路径同一套展平规则。
        AIContent content => RenderContent(content),
        JsonElement json => RenderJson(json),
        _ => RenderUnknown(result)
    };

    /// <summary>展平 AIContent 集合（MCP 多内容块返回值）；各部分以换行连接。</summary>
    internal static string JoinContents(IEnumerable<AIContent> contents) =>
        string.Join("\n", contents.Select(RenderContent).Where(part => !string.IsNullOrEmpty(part)));

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

    /// <summary>
    /// MCP 内嵌二进制资源经 SDK 投影成 DataContent 时，资源 URI 只存在于
    /// RawRepresentation（SDK 会用块级 _meta 覆盖 AdditionalProperties，uri 项不保）：
    /// 展平时从 RawRepresentation 带出来，模型才有线索凭 URI 再去获取该资源。
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

    /// <summary>
    /// McpClientTool 在结果带 isError/structuredContent/_meta，或含无法投影成
    /// AIContent 的内容块（如 resource_link）时，把整个 CallToolResult 序列化成
    /// JsonElement 返回。此处按与 AIContent 路径相同的规则展平，
    /// 而不是把原始 JSON（可能内含 base64 全文）灌给模型。
    /// </summary>
    private static string? RenderJson(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (LooksLikeCallToolResult(json))
        {
            try
            {
                if (JsonSerializer.Deserialize(json, typeof(CallToolResult), McpJsonUtilities.DefaultOptions)
                    is CallToolResult callToolResult)
                {
                    return RenderCallToolResult(callToolResult);
                }
            }
            catch (JsonException)
            {
                // 形似 CallToolResult 但结构损坏：退回原始 JSON，不吞数据。
            }
        }

        return json.GetRawText();
    }

    // 供 MCP 资源落盘管道复用同一形状判定，避免两处判定漂移。
    internal static bool LooksLikeCallToolResult(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object
        && ((json.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            || json.TryGetProperty("structuredContent", out _)
            || json.TryGetProperty("isError", out _));

    private static string RenderCallToolResult(CallToolResult result)
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

        // structuredContent 是模型可读的结构化数据，正文之后原样保留；
        // _meta 属协议层管道信息，对模型无意义，不参与渲染。
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured)
        {
            parts.Add(structured.GetRawText());
        }

        // MCP 的 isError 结果不抛异常：与 ErrorContent 一致地加前缀，模型才能
        // 与正常结果区分开并决定重试还是绕开。
        string joined = string.Join("\n", parts);
        return result.IsError == true && joined.Length > 0 ? $"[tool error] {joined}" : joined;
    }

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
