using System.Text.Json;

namespace OpenAgent.Engine.Reload.Dtos;

internal sealed class ConfigUpdate
{
    internal const string AgentResourceType = "Agent";
    internal const string LlmResourceType = "Llm";
    internal const string UpsertOperation = "Upsert";
    internal const string DeleteOperation = "Delete";

    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? Operation { get; set; }
    public string? AgentId { get; set; }
    public string? Type { get; set; }
    public string? ConfigType { get; set; }
    public JsonElement? Data { get; set; }
    public object? Version { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
