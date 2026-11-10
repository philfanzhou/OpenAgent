using System.Text.Json;

namespace OpenAgent.Contracts.Models;

public sealed class McpConnectionTestResult
{
    public bool Success { get; set; }
    public bool Connected { get; set; }
    public long LatencyMs { get; set; }
    public string? ServerName { get; set; }
    public string? ServerVersion { get; set; }
    public string? ProtocolVersion { get; set; }
    public IReadOnlyList<McpToolDefinition> Tools { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class McpToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public JsonElement InputSchema { get; set; }
    public JsonElement? OutputSchema { get; set; }
}
