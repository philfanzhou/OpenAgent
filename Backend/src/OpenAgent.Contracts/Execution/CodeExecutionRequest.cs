namespace OpenAgent.Contracts.Execution;

public sealed class CodeExecutionRequest
{
    public string Code { get; init; } = string.Empty;
    public List<ExecutionFile> Files { get; init; } = [];

    /// <summary>Runner language identifier; see <see cref="ExecutionLanguage"/>. Defaults to Python.</summary>
    public string Language { get; init; } = ExecutionLanguage.Python;
}
