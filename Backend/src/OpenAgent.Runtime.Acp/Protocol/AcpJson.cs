using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OpenAgent.Runtime.Acp.Protocol;

public static class AcpJson
{
    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)(AcpJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new NotSupportedException("Supply explicit JsonTypeInfo for extension types."));
    public static T Read<T>(JsonElement value) => value.Deserialize(TypeInfo<T>()) ?? throw new JsonException("Missing protocol value.");
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());
    public static JsonElement Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

// 这里只声明序列化元数据；编译器生成物留在 obj，不把几百个包装类纳入源码。
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    AllowOutOfOrderMetadataProperties = true, Converters = [typeof(OptionalConverter<string>),
    typeof(OptionalConverter<JsonElement>), typeof(OptionalConverter<ToolCallContent[]>),
    typeof(OptionalConverter<ToolLocation[]>), typeof(OptionalConverter<ContentBlock[]>)])]
[JsonSerializable(typeof(Dictionary<string, Empty>))]
[JsonSerializable(typeof(Empty))]
[JsonSerializable(typeof(Implementation))]
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(InitializeResponse))]
[JsonSerializable(typeof(ClientCapabilities))]
[JsonSerializable(typeof(AgentCapabilities))]
[JsonSerializable(typeof(SessionCapabilities))]
[JsonSerializable(typeof(AuthenticateRequest))]
[JsonSerializable(typeof(NewSessionRequest))]
[JsonSerializable(typeof(LoadSessionRequest))]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(ListSessionsRequest))]
[JsonSerializable(typeof(ListSessionsResponse))]
[JsonSerializable(typeof(SetModeRequest))]
[JsonSerializable(typeof(SetConfigRequest))]
[JsonSerializable(typeof(SetConfigResponse))]
[JsonSerializable(typeof(PromptRequest))]
[JsonSerializable(typeof(PromptResponse))]
[JsonSerializable(typeof(SessionNotification))]
[JsonSerializable(typeof(SessionUpdate))]
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(ToolCallUpdate))]
[JsonSerializable(typeof(ConfigOption))]
[JsonSerializable(typeof(RequestPermissionRequest))]
[JsonSerializable(typeof(PermissionResponse))]
[JsonSerializable(typeof(ReadFileRequest))]
[JsonSerializable(typeof(ReadFileResponse))]
[JsonSerializable(typeof(WriteFileRequest))]
[JsonSerializable(typeof(CreateTerminalRequest))]
[JsonSerializable(typeof(CreateTerminalResponse))]
[JsonSerializable(typeof(TerminalRequest))]
[JsonSerializable(typeof(TerminalOutputResponse))]
[JsonSerializable(typeof(ExitStatus))]
[JsonSerializable(typeof(ElicitationRequest))]
[JsonSerializable(typeof(ElicitationResponse))]
[JsonSerializable(typeof(ElicitationComplete))]
[JsonSerializable(typeof(CancelRequest))]
[JsonSerializable(typeof(RpcId))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ToolCallContent[]))]
[JsonSerializable(typeof(ToolLocation[]))]
[JsonSerializable(typeof(ContentBlock[]))]
[JsonSerializable(typeof(Rpc.RpcError))]
[JsonSerializable(typeof(Rpc.WireMessage))]
internal partial class AcpJsonContext : JsonSerializerContext;
