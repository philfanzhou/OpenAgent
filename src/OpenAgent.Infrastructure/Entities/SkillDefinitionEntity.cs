namespace OpenAgent.Infrastructure.Entities;

internal sealed class SkillDefinitionEntity
{
    public required string TenantId { get; init; }
    public required string SkillId { get; init; }
    public required string Type { get; init; }
    public required string SourceType { get; set; }
    public required string DefinitionJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
