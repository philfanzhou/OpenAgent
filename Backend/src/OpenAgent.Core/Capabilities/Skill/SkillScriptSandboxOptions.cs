namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class SkillScriptSandboxOptions
{
    internal const string SectionName = "SkillSandbox";

    public bool Enabled { get; set; }
    public string? UnixSocketPath { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxScriptBytes { get; set; } = 256 * 1024;
    public int MaxOutputBytes { get; set; } = 64 * 1024;
    public int MaxArgumentCount { get; set; } = 32;
    public int MaxArgumentLength { get; set; } = 4096;
    public List<string> AllowedExtensions { get; set; } = [".py"];
}
