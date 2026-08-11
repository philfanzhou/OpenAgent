using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentEndpointRequestMapper
{
    internal static AgentRequest CreateAgentRequest(ChatRequest request, HttpContext context)
    {
        AgentRequestFeature feature = context.GetAgentRequest();
        Dictionary<string, string>? externalContext = request.Context?
            .Where(item => !IsReservedChatContextKey(item.Key))
            .ToDictionary(item => item.Key, item => item.Value?.ToString() ?? string.Empty);
        return new AgentRequest
        {
            Query = request.Message,
            AgentId = context.Request.Headers[AgentRoutingHeaders.ResolvedAgentId].FirstOrDefault()
                ?? ReadContextValue(request.Context, "agentId")
                ?? context.Request.Headers["X-Agent-Id"].FirstOrDefault(),
            ConversationId = ReadContextValue(request.Context, "conversationId")
                ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault(),
            TraceId = feature.TraceId,
            ClientType = ClientType.Web,
            ExternalContext = externalContext
        };
    }

    internal static string RequireTenant(HttpContext context)
    {
        return context.GetAgentRequest().User.TenantId
            ?? throw new TenantDataIsolationException(null, null, "TenantId is required but not provided");
    }

    private static string? ReadContextValue(
        IReadOnlyDictionary<string, object>? context,
        string key)
    {
        if (context == null || !context.TryGetValue(key, out object? value))
        {
            return null;
        }

        return value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value?.ToString();
    }

    private static bool IsReservedChatContextKey(string key) =>
        key.Equals("agentId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("conversationId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("traceId", StringComparison.OrdinalIgnoreCase);
}
