namespace OpenAgent.Contracts.Skills;

public class SkillDescriptor
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ParametersJsonSchema { get; init; } = string.Empty;
    public SkillSource Source { get; init; }
    public string? SourceId { get; init; }

    public List<string> AllowedUserIds { get; init; } = new();
    public List<string> AllowedGroups { get; init; } = new();
    public List<string> AllowedTenantIds { get; init; } = new();
    public List<string> AllowedRoles { get; init; } = new();
}

public enum SkillSource
{
    Local,
    Mcp,
    Matrix
}
