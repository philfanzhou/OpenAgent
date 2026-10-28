using System.Text.Json;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Hosting;

/// <summary>
/// <see cref="ChatRequest.Context"/> 保留键与解析的单一来源，Router 与 Engine.Host 共用。
/// 放在 Hosting 是因为架构约束 Router 只能引用 Contracts 与 Hosting；
/// Contracts 保持纯接口/DTO，不含具体方法。
/// </summary>
public static class ChatRequestContext
{
    public const string ConversationIdKey = "conversationId";
    public const string AgentIdKey = "agentId";
    public const string LlmProfileIdKey = "llmProfileId";
    public const string ConversationTypeKey = "conversationType";
    public const string ClientTypeKey = "clientType";
    public const string TraceIdKey = "traceId";
    private static readonly string[] ReservedKeys =
        [ConversationIdKey, AgentIdKey, LlmProfileIdKey, ConversationTypeKey, ClientTypeKey, TraceIdKey];

    /// <summary>
    /// 忽略大小写读取字符串值；值必须是字符串（JsonElement/CLR string）或 null，
    /// 否则抛出 <see cref="JsonException"/>。
    /// </summary>
    public static string? ReadString(Dictionary<string, object>? context, string key)
    {
        KeyValuePair<string, object> entry = context?.FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? default;
        if (entry.Value == null)
        {
            return null;
        }

        return entry.Value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            string value => value,
            _ => throw new JsonException(
                $"The chat request context property '{key}' must be a string.")
        };
    }

    /// <summary>判断是否保留键（忽略大小写）。</summary>
    public static bool IsReservedKey(string key) =>
        ReservedKeys.Any(reserved => key.Equals(reserved, StringComparison.OrdinalIgnoreCase));
}
