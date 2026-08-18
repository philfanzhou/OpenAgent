namespace OpenAgent.Core.Capabilities.Mcp;

public sealed class McpExecutionOptions
{
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public bool AllowStdio { get; set; }
    public bool AllowUnlistedCommands { get; set; }
    public List<string> AllowedCommands { get; set; } = new();
    public List<string> AllowedEnvironmentVariables { get; set; } = new();
    public List<string> AllowedWorkingDirectories { get; set; } = new();
}
