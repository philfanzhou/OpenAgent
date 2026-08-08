using OpenAgent.Contracts.Security;
using OpenAgent.Router.Options;

namespace OpenAgent.Router;

internal interface IExternalAgentAdapter
{
    string Name { get; }

    Uri BuildTargetUri(ExternalAgentOptions agent, string? action);

    ValueTask ApplyAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        ExternalAgentOptions agent,
        IAgentUserContext userContext,
        string? tenantId,
        string? conversationId,
        string traceId);
}
