namespace OpenAgent.Contracts.Execution;

public sealed class CodeExecutionResult
{
    public string ExecutionId { get; set; } = string.Empty;
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public List<ExecutionFile> Files { get; init; } = [];
}
