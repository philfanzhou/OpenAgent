using System.IO.Compression;

using OpenAgent.Contracts.Skills;

namespace OpenAgent.Application.Capabilities.Skill;

public sealed record SkillPackageFile(string RelativePath, byte[] Content);
