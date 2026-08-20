namespace OpenAgent.Infrastructure.Entities;

internal sealed class AgentConfigurationEntity
{
    public required string AgentId { get; init; }
    public required string TenantId { get; set; }
    public required string ConfigurationJson { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
