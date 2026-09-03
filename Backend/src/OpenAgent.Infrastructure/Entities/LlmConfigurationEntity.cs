using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Infrastructure.Entities;

internal sealed class LlmConfigurationEntity
{
    public required string TenantId { get; set; }
    public required string ProfileId { get; init; }
    public string Name { get; set; } = string.Empty;
    public ApiFormat Format { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
    public int ContextTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public bool SupportsMaxOutputTokens { get; set; } = true;
    public ModelModality Modality { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
