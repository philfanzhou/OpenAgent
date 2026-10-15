using System.IO.Compression;
using Microsoft.Agents.AI;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Application.Capabilities.Skill;

public sealed record AgentSkillPackageMetadata(
    string Name,
    string Description,
    int SkillCount,
    int ResourceCount,
    IReadOnlyList<string> ScriptNames);
