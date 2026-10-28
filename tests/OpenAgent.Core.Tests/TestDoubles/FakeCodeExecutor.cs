using OpenAgent.Contracts.Execution;

namespace OpenAgent.Core.Tests.TestDoubles;

/// <summary>
/// In-memory code executor capturing requests and replaying queued results.
/// </summary>
internal sealed class FakeCodeExecutor : ICodeExecutor
{
    internal List<CodeExecutionRequest> Requests { get; } = [];
    internal Queue<CodeExecutionResult> Results { get; } = new();

    public Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Results.TryDequeue(out CodeExecutionResult? result) ? result : new CodeExecutionResult());
    }
}
