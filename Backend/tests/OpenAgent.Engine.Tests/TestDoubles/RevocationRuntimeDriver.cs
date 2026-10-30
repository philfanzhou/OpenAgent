using System.Runtime.CompilerServices;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Engine.Tests.TestDoubles;

internal sealed class RevocationRuntimeDriver : IAgentRuntimeDriver
{
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            yield return new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = "partial" };
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally { Disposed.TrySetResult(); }
    }
}
