using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IExternalAgentForwarder
{
    Task<ExternalForwardingResult?> ForwardAsync(
        HttpContext context,
        string agentId,
        string? action,
        IAgentUserContext userContext,
        string? tenantId,
        string? conversationId,
        string traceId,
        CancellationToken cancellationToken);
}
