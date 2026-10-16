using System.Text.Json;
using System.Text.Json.Serialization;
namespace OpenAgent.Runtime.Acp.Protocol;

public record AcpData
{
    [JsonPropertyName("_meta")] public JsonElement? Meta { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
}
public sealed record Empty : AcpData;
public sealed record Implementation(string Name, string Version, string? Title = null) : AcpData;
public sealed record InitializeRequest([property: JsonRequired] int ProtocolVersion = 1, ClientCapabilities? ClientCapabilities = null, Implementation? ClientInfo = null) : AcpData;
public sealed record InitializeResponse([property: JsonRequired] int ProtocolVersion = 1, AgentCapabilities? AgentCapabilities = null, AuthMethod[]? AuthMethods = null, Implementation? AgentInfo = null) : AcpData;
public sealed record ClientCapabilities(FileSystemCapabilities? Fs = null, bool Terminal = false, JsonElement? Session = null, JsonElement? Auth = null, JsonElement? Elicitation = null) : AcpData;
public sealed record FileSystemCapabilities(bool ReadTextFile = false, bool WriteTextFile = false);
public sealed record AgentCapabilities(bool LoadSession = false, PromptCapabilities? PromptCapabilities = null, JsonElement? McpCapabilities = null, SessionCapabilities? SessionCapabilities = null, JsonElement? Auth = null) : AcpData;
public sealed record PromptCapabilities(bool Image = false, bool Audio = false, bool EmbeddedContext = false);
public sealed record SessionCapabilities(Empty? List = null, Empty? Delete = null, Empty? Resume = null, Empty? Close = null, Empty? AdditionalDirectories = null) : AcpData;
public sealed record AuthMethod(string Id, string Name, string? Description = null, string? Type = null) : AcpData
{
    public string[]? Args { get; init; }
    public Dictionary<string, string>? Env { get; init; }
}
public sealed record AuthenticateRequest(string MethodId) : AcpData;
public sealed record EnvVariable(string Name, string Value);
public sealed record McpServer(string Name) : AcpData
{
    public string? Type { get; init; }
    public string? Command { get; init; }
    public string[]? Args { get; init; }
    public EnvVariable[]? Env { get; init; }
    public string? Url { get; init; }
    public EnvVariable[]? Headers { get; init; }
}
