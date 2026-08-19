using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IAgentSelectionService
{
    Task<AgentSelection?> SelectAsync(
        string message,
        string tenantId,
        string? conversationId,
        string? explicitAgentId,
        CancellationToken cancellationToken,
        string? authenticationToken = null);
}
