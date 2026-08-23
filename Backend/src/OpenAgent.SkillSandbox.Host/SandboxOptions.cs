namespace OpenAgent.SkillSandbox.Host;

internal sealed class SandboxOptions
{
    internal const string SectionName = "Sandbox";

    public string SocketPath { get; set; } = "/run/openagent-sandbox/sandbox.sock";
    public string Interpreter { get; set; } = "/usr/bin/python3";
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxScriptBytes { get; set; } = 256 * 1024;
    public int MaxOutputBytes { get; set; } = 64 * 1024;
    public int MaxArgumentCount { get; set; } = 32;
    public int MaxArgumentLength { get; set; } = 4096;
    public List<string> AllowedExtensions { get; set; } = [".py"];
}
