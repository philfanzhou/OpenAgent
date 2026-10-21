using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Application.Execution;

internal interface IAgentExecution
{
    Task<AgentResponse> ExecuteAsync(AgentRequest request, IAgentUserContext user, CancellationToken cancellationToken);
    IAsyncEnumerable<AgentStreamEvent> ExecuteStreamingAsync(AgentRequest request, IAgentUserContext user, CancellationToken cancellationToken);
}
