using OpenAgent.Application.Execution;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Application.Runtime.Agent;

/// <summary>保留对端点的稳定入口；具体推理循环由运行时驱动负责。</summary>
public sealed class AgentExecutor
{
    private readonly IAgentExecution _execution;

    internal AgentExecutor(IAgentExecution execution)
    {
        _execution = execution;
    }

    public Task<AgentResponse> ExecuteAsync(AgentRequest request, IAgentUserContext user, CancellationToken cancellationToken)
    {
        return _execution.ExecuteAsync(request, user, cancellationToken);
    }

    public IAsyncEnumerable<AgentStreamEvent> ExecuteStreamingAsync(AgentRequest request, IAgentUserContext user, CancellationToken cancellationToken)
    {
        return _execution.ExecuteStreamingAsync(request, user, cancellationToken);
    }
}
