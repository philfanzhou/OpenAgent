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
        foreach (string header in new[]
        {
            "Authorization",
            "X-Agent-Id",
            AgentRoutingHeaders.ResolvedAgentId,
            "X-Conversation-Id",
            "X-Trace-Id",
            "X-User-Id",
            "X-Tenant-Id",
            "X-TenantId",
            DelegatedAuthorizationHeaders.Grant
        })
        {
            proxyRequest.Headers.Remove(header);
        }

        proxyRequest.Headers.Add("X-User-Id", userContext.UserId);
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            proxyRequest.Headers.Add("X-Tenant-Id", tenantId);
        }
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            proxyRequest.Headers.Add(AgentRoutingHeaders.ResolvedAgentId, agentId);
        }
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            proxyRequest.Headers.Add("X-Conversation-Id", conversationId);
        }
        proxyRequest.Headers.Add("X-Trace-Id", traceId);
        proxyRequest.Headers.Add(DelegatedAuthorizationHeaders.Grant, gatewayGrant);
        return ValueTask.CompletedTask;
    }
}
