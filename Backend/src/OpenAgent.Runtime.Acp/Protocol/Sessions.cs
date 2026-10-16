using System.Text.Json;
namespace OpenAgent.Runtime.Acp.Protocol;

public sealed record NewSessionRequest(string Cwd, McpServer[] McpServers, string[]? AdditionalDirectories = null) : AcpData;
public sealed record LoadSessionRequest(string SessionId, string Cwd, McpServer[] McpServers, string[]? AdditionalDirectories = null) : AcpData;
public sealed record SessionRequest(string SessionId) : AcpData;
public sealed record SessionResponse(string? SessionId = null, SessionModes? Modes = null, ConfigOption[]? ConfigOptions = null) : AcpData;
public sealed record ListSessionsRequest(string? Cwd = null, string? Cursor = null) : AcpData;
public sealed record ListSessionsResponse(SessionInfo[] Sessions, string? NextCursor = null) : AcpData;
public sealed record SessionInfo(string SessionId, string Cwd, string? Title = null, string? UpdatedAt = null, string[]? AdditionalDirectories = null) : AcpData;
public sealed record SessionMode(string Id, string Name, string? Description = null) : AcpData;
public sealed record SessionModes(string CurrentModeId, SessionMode[] AvailableModes) : AcpData;
public sealed record SetModeRequest(string SessionId, string ModeId) : AcpData;
public sealed record ConfigOption(string Id, string Name, string Type, JsonElement CurrentValue) : AcpData
{
    public string? Description { get; init; }
    public string? Category { get; init; }
    public JsonElement? Options { get; init; }
}
public sealed record SetConfigRequest(string SessionId, string ConfigId, JsonElement Value, string? Type = null) : AcpData;
public sealed record SetConfigResponse(ConfigOption[] ConfigOptions) : AcpData;
public sealed record PromptRequest(string SessionId, ContentBlock[] Prompt) : AcpData;
public sealed record PromptResponse(string StopReason) : AcpData;
public sealed record SessionNotification(string SessionId, SessionUpdate Update) : AcpData;
