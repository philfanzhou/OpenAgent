namespace OpenAgent.Core.Execution;

internal sealed class AcpHarness
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = [];
    public bool OpenCode { get; set; }
    // Native harnesses own their model and authentication; platform profiles are opt-in.
    public bool UsePlatformModel { get; set; }
    public string? Model { get; set; }
    public string? ModelEnvironmentVariable { get; set; }
    public string EnvironmentMode { get; set; } = "isolated";
    public List<string> InheritedEnvironmentVariables { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public string ModelIdTemplate { get; set; } = "{provider}/{model}";
    public string PermissionPolicy { get; set; } = "deny";
}
