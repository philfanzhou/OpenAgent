using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IAgentSelectionService
{
    Task<AgentSelection?> SelectAsync(
        string message,
        string? conversationId,
        string? explicitAgentId,
        CancellationToken cancellationToken,
        string? authenticationToken = null,
        string? llmProfileId = null);
}
