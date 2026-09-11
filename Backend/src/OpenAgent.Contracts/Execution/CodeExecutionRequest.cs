namespace OpenAgent.Contracts.Execution;

public sealed class CodeExecutionRequest
{
    public string Code { get; init; } = string.Empty;
    public List<ExecutionFile> Files { get; init; } = [];
}
