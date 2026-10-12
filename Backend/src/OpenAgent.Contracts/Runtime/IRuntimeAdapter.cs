using OpenAgent.Contracts.Requests;

namespace OpenAgent.Contracts.Runtime;

public interface IRuntimeAdapter
{
    string Id { get; }
    IReadOnlyList<RuntimeDescriptor> Describe();
    Task<AgentResponse> ExecuteAsync(RuntimeInvocation invocation, CancellationToken cancellationToken);
    IAsyncEnumerable<AgentStreamEvent> ExecuteStreamingAsync(RuntimeInvocation invocation, CancellationToken cancellationToken);
}
