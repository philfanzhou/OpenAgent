using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 把工具结果安全渲染为文本。MCP 工具返回多个内容块时结果形如
/// <see cref="AIContent"/> 数组，直接 <c>ToString()</c> 只会得到类型名
/// （"Microsoft.Extensions.AI.AIContent[]"）：该字符串既被持久化进会话历史，
/// 又原样回喂给模型，模型看到无意义输出后会反复空参重试同一工具。
/// 所有展示、持久化与回喂模型的工具结果都应经由此处展平为可读文本。
/// </summary>
internal static class ToolResultText
{
    internal static string? Render(object? result) => result switch
    {
        null => null,
        string text => text,
        ToolResult toolResult => toolResult.Content,
        TextContent text => text.Text,
        IEnumerable<AIContent> contents => JoinContents(contents),
        JsonElement json => json.ValueKind == JsonValueKind.Undefined ? null : json.GetRawText(),
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
        DataContent { Uri: { } uri } when !uri.StartsWith(
            "data:", StringComparison.OrdinalIgnoreCase) => uri,
        DataContent data => data.Data.Length > 0
            ? $"[binary content: {data.MediaType}, {data.Data.Length} bytes]"
            : string.Empty,
        UriContent { Uri: { } uri } => uri.ToString(),
        _ => string.Empty
    };

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
