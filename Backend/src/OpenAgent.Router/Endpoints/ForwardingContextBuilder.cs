using OpenAgent.Authorization;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Endpoints;

internal static class ForwardingContextBuilder
{
    internal static ValueTask ApplyAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        IAgentUserContext userContext,
        string? tenantId,
        string? agentId,
        string? conversationId,
        string traceId,
        string gatewayGrant)
    {
        proxyRequest.RequestUri = targetUri;
        proxyRequest.Headers.Remove("X-Agent-Id");
        proxyRequest.Headers.Remove(AgentRoutingHeaders.ResolvedAgentId);
        proxyRequest.Headers.Remove("X-Conversation-Id");
        proxyRequest.Headers.Remove("X-Trace-Id");
        proxyRequest.Headers.Remove("X-User-Id");
        proxyRequest.Headers.Remove("X-Tenant-Id");
        proxyRequest.Headers.Remove("Authorization");
        proxyRequest.Headers.Remove(DelegatedPermissionHeaders.Grant);
        proxyRequest.Headers.Add("X-User-Id", userContext.UserId);
        if (!string.IsNullOrEmpty(tenantId)) proxyRequest.Headers.Add("X-Tenant-Id", tenantId);
        proxyRequest.Headers.Add("X-Trace-Id", traceId);
        proxyRequest.Headers.Add(DelegatedPermissionHeaders.Grant, gatewayGrant);
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            proxyRequest.Headers.Add(AgentRoutingHeaders.ResolvedAgentId, agentId);
        }
        if (!string.IsNullOrEmpty(conversationId)) proxyRequest.Headers.Add("X-Conversation-Id", conversationId);
        return ValueTask.CompletedTask;
    }
}
