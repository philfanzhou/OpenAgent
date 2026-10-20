using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Runtime.Acp.Protocol;

// 更新通过 sessionUpdate 判别字段选择类型；工具补丁不会把省略字段误写成 null。
[JsonPolymorphic(TypeDiscriminatorPropertyName = "sessionUpdate")]
[JsonDerivedType(typeof(UserMessageChunk), "user_message_chunk")]
[JsonDerivedType(typeof(AgentMessageChunk), "agent_message_chunk")]
[JsonDerivedType(typeof(AgentThoughtChunk), "agent_thought_chunk")]
[JsonDerivedType(typeof(ToolCall), "tool_call")]
[JsonDerivedType(typeof(ToolCallUpdate), "tool_call_update")]
[JsonDerivedType(typeof(PlanUpdate), "plan")]
[JsonDerivedType(typeof(CommandsUpdate), "available_commands_update")]
[JsonDerivedType(typeof(ModeUpdate), "current_mode_update")]
[JsonDerivedType(typeof(ConfigUpdate), "config_option_update")]
[JsonDerivedType(typeof(InfoUpdate), "session_info_update")]
[JsonDerivedType(typeof(UsageUpdate), "usage_update")]
[JsonDerivedType(typeof(NoticeUpdate), "notice")]
[JsonDerivedType(typeof(CompactionUpdate), "compaction_update")]
[JsonDerivedType(typeof(CompactionSummaryChunk), "compaction_summary_chunk")]
public abstract record SessionUpdate : AcpData;
public sealed record UserMessageChunk(ContentBlock Content, string? MessageId = null) : SessionUpdate;
public sealed record AgentMessageChunk(ContentBlock Content, string? MessageId = null) : SessionUpdate;
public sealed record AgentThoughtChunk(ContentBlock Content, string? MessageId = null) : SessionUpdate;
public sealed record ToolCall(string ToolCallId, string Title) : SessionUpdate
{
    public string? Name
    {
        get; init;
    }
    public string Kind { get; init; } = "other";
    public string Status { get; init; } = "pending";
    public ToolCallContent[]? Content
    {
        get; init;
    }
    public ToolLocation[]? Locations
    {
        get; init;
    }
    public JsonElement? RawInput
    {
        get; init;
    }
    public JsonElement? RawOutput
    {
        get; init;
    }
}
public sealed record ToolCallUpdate(string ToolCallId) : SessionUpdate
{
    // 补丁字段需要区分省略与显式 null；普通 DTO 不付出这项额外复杂度。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Title
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Name
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Kind
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Status
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<ToolCallContent[]> Content
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<ToolLocation[]> Locations
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<JsonElement> RawInput
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<JsonElement> RawOutput
    {
        get; init;
    }
}
public sealed record PlanEntry(string Content, string Priority, string Status) : AcpData;
public sealed record PlanUpdate(PlanEntry[] Entries) : SessionUpdate;
public sealed record AvailableCommand(string Name, string Description, JsonElement? Input = null) : AcpData;
public sealed record CommandsUpdate(AvailableCommand[] AvailableCommands) : SessionUpdate;
public sealed record ModeUpdate(string CurrentModeId) : SessionUpdate;
public sealed record ConfigUpdate(ConfigOption[] ConfigOptions) : SessionUpdate;
public sealed record InfoUpdate : SessionUpdate
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Title
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> UpdatedAt
    {
        get; init;
    }
}
public sealed record UsageUpdate(long Used, long Size, JsonElement? Cost = null) : SessionUpdate;
public sealed record NoticeUpdate(string Severity, string Title, string? Description = null) : SessionUpdate;
public sealed record CompactionUpdate(string CompactionId, string Status) : SessionUpdate
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<ContentBlock[]> Summary
    {
        get; init;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Error
    {
        get; init;
    }
}
public sealed record CompactionSummaryChunk(string CompactionId, ContentBlock Content) : SessionUpdate;
