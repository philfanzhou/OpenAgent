namespace OpenAgent.Runner;

internal sealed class RunnerOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BubblewrapPath { get; set; } = "/usr/bin/bwrap";
    public string PythonPath { get; set; } = "/opt/openagent-code/venv/bin/python";
    public string WorkspaceRoot { get; set; } = "/var/lib/openagent-runner/workspaces";
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxConcurrentExecutions { get; set; } = 2;
    public int MemoryMiB { get; set; } = 1536;
    public int WorkspaceMiB { get; set; } = 128;
    public int MaxProcesses { get; set; } = 64;
}
