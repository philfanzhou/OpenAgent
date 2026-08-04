using System.Text.Json;

namespace OpenAgent.Engine.Reload.Dtos;

internal sealed class ConfigUpdate
{
    public string? AgentId { get; set; }
    public string? Type { get; set; }
    public string? ConfigType { get; set; }
    public JsonElement? Data { get; set; }
    public long Version { get; set; }
    public DateTime Timestamp { get; set; }
}
