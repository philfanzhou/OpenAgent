namespace OpenAgent.Contracts.Skills;

public sealed record AgentSkillFrontmatter(string Name, string Description, string Compatibility);

public sealed record AgentSkillPackageMetadata(
    string Name,
    string Description,
    int SkillCount,
    int ResourceCount);
