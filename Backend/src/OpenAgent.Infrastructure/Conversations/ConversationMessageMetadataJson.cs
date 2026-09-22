using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Infrastructure;

/// <summary>
/// conversation_messages.metadata_json 列的唯一序列化边界。
/// 写入始终输出新形态（JsonSerializerDefaults.Web，camelCase，与 Redis 热缓存同语义）；
/// 读取兼容两种历史形态：
/// <list type="bullet">
/// <item>旧形态：string→string 字典（PascalCase 键 "Files"/"Reasoning"/"ToolArguments"/"ExecutionStatus"，
/// 其中 "Files" 的值是再序列化一次的 JSON 数组，内层元素属性 camelCase/PascalCase 均可解析）；</item>
/// <item>新形态：ConversationMessageMetadata 对象（camelCase 属性名）。</list>
/// 未知键与无法解析的键值降级进 <see cref="ConversationMessageMetadata.Extensions"/> 原样保留，
/// 整个载荷无法解析时以保留键存入原始 JSON 并告警，不再静默返回 null。
/// </summary>
internal static partial class ConversationMessageMetadataJson
{
    /// <summary>整载荷解析失败时保留原始 JSON 的 Extensions 键（保留现场，供排查与后续修复）。</summary>
    internal const string RawJsonExtensionKey = "__rawMetadataJson";

    private const string FilesKey = "files";
    private const string ReasoningKey = "reasoning";
    private const string ExecutionStatusKey = "executionstatus";
    private const string ToolArgumentsKey = "toolarguments";
    private const string ExtensionsKey = "extensions";

    /// <summary>EF 写路径与 Redis 缓存共用的 camelCase 语义（字典键不做改写）。</summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static string? Serialize(ConversationMessageMetadata? metadata) =>
        metadata == null ? null : JsonSerializer.Serialize(metadata, Options);

    /// <summary>
    /// 大小写不敏感的逐键解析：标量键在新旧两种形态下值形状一致（字符串），
    /// "files" 按值形状分派（数组=新形态；字符串=旧形态的内层 JSON），
    /// 因此无需先探测形态再分派即可同时覆盖两种载荷。
    /// </summary>
    internal static ConversationMessageMetadata? Deserialize(string? json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        // LoggerMessage 生成代码假定非空 logger；解析告警不能因调用方省略 logger 而崩。
        logger ??= NullLogger.Instance;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            LogPayloadParseFailure(logger, exception);
            return PreserveRaw(json);
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                LogPayloadParseFailure(logger, null);
                return PreserveRaw(json);
            }

            ConversationMessageMetadata metadata = new();
            foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case FilesKey:
                        metadata.Files = ReadFiles(property, metadata, logger);
                        break;
                    case ReasoningKey:
                        metadata.Reasoning = ReadString(property, metadata, logger);
                        break;
                    case ExecutionStatusKey:
                        metadata.ExecutionStatus = ReadString(property, metadata, logger);
                        break;
                    case ToolArgumentsKey:
                        metadata.ToolArguments = ReadString(property, metadata, logger);
                        break;
                    case ExtensionsKey when property.Value.ValueKind == JsonValueKind.Object:
                        ReadExtensions(property, metadata);
                        break;
                    default:
                        PreserveUnknown(property, metadata);
                        break;
                }
            }

            return metadata;
        }

        static ConversationMessageMetadata PreserveRaw(string raw)
        {
            var preserved = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RawJsonExtensionKey] = raw
            };
            return new ConversationMessageMetadata { Extensions = preserved };
        }
    }

    private static List<MessageFileMetadata>? ReadFiles(
        JsonProperty property,
        ConversationMessageMetadata metadata,
        ILogger logger)
    {
        // 旧形态把附件数组二次序列化成字符串；新形态直接是数组。
        string payload = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString() ?? string.Empty
            : property.Value.GetRawText();
        try
        {
            return JsonSerializer.Deserialize<List<MessageFileMetadata>>(payload, Options);
        }
        catch (JsonException exception)
        {
            LogValueParseFailure(logger, exception, property.Name);
            PreserveUnknownValue(metadata, property.Name, payload);
            return null;
        }
    }

    private static string? ReadString(
        JsonProperty property,
        ConversationMessageMetadata metadata,
        ILogger logger)
    {
        if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
        {
            return property.Value.GetString();
        }

        LogValueParseFailure(logger, null, property.Name);
        PreserveUnknown(property, metadata);
        return null;
    }

    private static void ReadExtensions(JsonProperty property, ConversationMessageMetadata metadata)
    {
        metadata.Extensions ??= new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in property.Value.EnumerateObject())
        {
            metadata.Extensions[entry.Name] = entry.Value.ValueKind == JsonValueKind.String
                ? entry.Value.GetString() ?? string.Empty
                : entry.Value.GetRawText();
        }
    }

    private static void PreserveUnknown(JsonProperty property, ConversationMessageMetadata metadata) =>
        PreserveUnknownValue(
            metadata,
            property.Name,
            property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText());

    private static void PreserveUnknownValue(
        ConversationMessageMetadata metadata,
        string name,
        string rawValue)
    {
        metadata.Extensions ??= new Dictionary<string, string>(StringComparer.Ordinal);
        metadata.Extensions[name] = rawValue;
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "Conversation message metadata payload is not a parsable JSON object; preserved raw payload in extensions")]
    internal static partial void LogPayloadParseFailure(ILogger logger, Exception? exception);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning,
        Message = "Conversation message metadata key '{Key}' could not be parsed into its typed shape; preserved raw value in extensions")]
    internal static partial void LogValueParseFailure(ILogger logger, Exception? exception, string key);
}
