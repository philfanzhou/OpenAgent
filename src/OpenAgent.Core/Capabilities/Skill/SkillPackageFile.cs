using System.IO.Compression;
using Microsoft.Agents.AI;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

public sealed record SkillPackageFile(string RelativePath, byte[] Content);
