using OpenAgent.Contracts.Models;

namespace OpenAgent.Infrastructure.Entities;

internal sealed class AgentConfigurationEntity
{
    public required string AgentId { get; init; }
    public required string TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AgentPublishStatus Status { get; set; }
    public string Instructions { get; set; } = string.Empty;
    public int MaxTurns { get; set; } = 50;
    public string? ContextPolicyJson { get; set; }
    public string McpJson { get; set; } = "{}";
    public string RagJson { get; set; } = "{}";
    public string SkillsJson { get; set; } = "{}";
    public string CodeExecutionJson { get; set; } = "{}";
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
