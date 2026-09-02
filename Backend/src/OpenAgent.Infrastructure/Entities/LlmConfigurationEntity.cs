namespace OpenAgent.Infrastructure.Entities;

internal sealed class LlmConfigurationEntity
{
    public required string TenantId { get; set; }
    public required string ProfileId { get; init; }
    public required string ConfigurationJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
