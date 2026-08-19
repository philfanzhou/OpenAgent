namespace OpenAgent.Contracts.Skills;

/// <summary>
/// Object-storage index for an extracted Agent Skill package.
/// The index is platform metadata; the Skill files themselves remain ordinary
/// files under their original relative paths.
/// </summary>
public sealed class SkillPackageStorageIndex
{
    public int Version { get; set; } = 1;
    public string TenantId { get; set; } = string.Empty;
    public List<SkillPackageStorageFile> Files { get; set; } = new();
}

public sealed class SkillPackageStorageFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
