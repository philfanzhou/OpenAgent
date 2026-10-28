namespace OpenAgent.Contracts.Execution;

public sealed class CodeExecutionResult
{
    public string ExecutionId { get; set; } = string.Empty;
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public List<ExecutionFile> Files { get; init; } = [];

    /// <summary>
    /// True when the session sandbox had died (idle eviction, crash, or restart) and the
    /// Runner transparently started a fresh one for this execution, so previously
    /// persisted session files are gone.
    /// </summary>
    public bool SandboxReset { get; set; }
}
