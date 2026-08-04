using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Skills;

public class SkillMetadata
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public string? JsonSchema { get; init; }
    public IReadOnlyList<string>? RequiredClaims { get; init; }
    public IReadOnlyList<string>? RequiredRoles { get; init; }
    public bool RequiresHumanApproval { get; init; } = false;
}

public class McpMetadata
{
    public required string ServerName { get; init; }
    public required string ServerUrl { get; init; }
    public McpServerType ConnectionType { get; init; } = McpServerType.Http;
    public IReadOnlyList<string>? RequiredClaims { get; init; }
}

public class MatrixSkillMetadata
{
    public string SkillId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string JsonSchema { get; set; } = string.Empty;
    public List<string> RequiredClaims { get; set; } = new();
    public string Endpoint { get; set; } = string.Empty;
}
