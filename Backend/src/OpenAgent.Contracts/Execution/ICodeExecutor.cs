namespace OpenAgent.Contracts.Execution;

public interface ICodeExecutor
{
    Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken);
}
