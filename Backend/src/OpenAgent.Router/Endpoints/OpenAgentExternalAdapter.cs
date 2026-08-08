using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Endpoints;

internal sealed class OpenAgentExternalAdapter : IExternalAgentAdapter
{
    internal const string AdapterName = "OpenAgent";

    public string Name => AdapterName;

    public Uri BuildTargetUri(ExternalAgentOptions agent, string? action)
    {
        if (!ForwardingPathValidator.IsSafeAction(action))
        {
            throw new ArgumentException("The chat action contains an unsafe path segment.", nameof(action));
        }

        string suffix = string.IsNullOrWhiteSpace(action)
            ? string.Empty
            : $"/{action.Trim('/')}";
        return new Uri(
            $"{agent.BaseUrl.TrimEnd('/')}{agent.ChatPath.TrimEnd('/')}{suffix}",
            UriKind.Absolute);
    }

    public ValueTask ApplyAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        ExternalAgentOptions agent,
        IAgentUserContext userContext,
        string? tenantId,
        string? conversationId,
        string traceId,
        string? gatewayGrant)
    {
        proxyRequest.RequestUri = targetUri;
        RemoveGatewayHeaders(proxyRequest);
        proxyRequest.Headers.TryAddWithoutValidation("X-Trace-Id", traceId);
        if (!string.IsNullOrWhiteSpace(gatewayGrant))
        {
            proxyRequest.Headers.TryAddWithoutValidation(
                GatewayAuthorizationDefaults.GrantHeaderName,
                gatewayGrant);
        }

        string remoteAgentId = string.IsNullOrWhiteSpace(agent.RemoteAgentId)
            ? agent.AgentId
            : agent.RemoteAgentId;
        proxyRequest.Headers.TryAddWithoutValidation(
            AgentRoutingHeaders.ResolvedAgentId,
            remoteAgentId);
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            proxyRequest.Headers.TryAddWithoutValidation(
                "X-Conversation-Id",
                conversationId);
        }

        if (agent.ForwardIdentityHeaders)
        {
            proxyRequest.Headers.TryAddWithoutValidation("X-User-Id", userContext.UserId);
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                proxyRequest.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId);
            }
        }

        ExternalAgentAuthenticationOptions authentication = agent.Authentication;
        if (!string.IsNullOrWhiteSpace(authentication.Token))
        {
            string value = string.IsNullOrWhiteSpace(authentication.Scheme)
                ? authentication.Token
                : $"{authentication.Scheme} {authentication.Token}";
            proxyRequest.Headers.Remove(authentication.HeaderName);
            proxyRequest.Headers.TryAddWithoutValidation(authentication.HeaderName, value);
        }

        return ValueTask.CompletedTask;
    }

    private static void RemoveGatewayHeaders(HttpRequestMessage proxyRequest)
    {
        string[] accept = proxyRequest.Headers.Accept
            .Select(value => value.ToString())
            .ToArray();
        proxyRequest.Headers.Clear();
        foreach (string value in accept)
        {
            proxyRequest.Headers.TryAddWithoutValidation("Accept", value);
        }
    }
}
