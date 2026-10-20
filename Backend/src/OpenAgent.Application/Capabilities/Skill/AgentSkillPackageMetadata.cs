using System.IO.Compression;

using OpenAgent.Contracts.Skills;

namespace OpenAgent.Application.Capabilities.Skill;

public sealed record AgentSkillFrontmatter(string Name, string Description, string Compatibility);

public sealed record AgentSkillPackageMetadata(
    string Name,
    string Description,
    int SkillCount,
    int ResourceCount,
    IReadOnlyList<string> ScriptNames);
