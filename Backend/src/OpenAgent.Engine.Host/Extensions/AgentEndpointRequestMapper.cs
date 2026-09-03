using System.Text.Json;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentEndpointRequestMapper
{
    internal static AgentRequest CreateAgentRequest(
        ChatRequest request,
        HttpContext context,
        bool createConversation = true)
    {
        AgentRequestFeature feature = context.GetAgentRequest();
        string? conversationId = createConversation
            ? ReadContextValue(request.Context, "conversationId")
                ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault()
                ?? Guid.NewGuid().ToString()
            : null;
        Dictionary<string, string>? externalContext = request.Context?
            .Where(item => !IsReservedChatContextKey(item.Key))
            .ToDictionary(item => item.Key, item => item.Value?.ToString() ?? string.Empty);
        return new AgentRequest
        {
            Query = request.Message,
            AgentId = ReadContextValue(request.Context, "agentId")
                ?? context.Request.Headers["X-Agent-Id"].FirstOrDefault(),
            LlmProfileId = ReadContextValue(request.Context, "llmProfileId")
                ?? context.Request.Headers["X-OpenAgent-Llm-Profile-Id"].FirstOrDefault(),
            ConversationId = conversationId,
            ConversationType = ReadContextEnum(
                request.Context,
                "conversationType",
                ConversationType.User),
            TraceId = feature.TraceId,
            ClientType = ReadContextEnum(
                request.Context,
                "clientType",
                ClientType.Web),
            ContextWindowTokens = request.ContextWindowTokens,
            MaxOutputTokens = request.MaxOutputTokens,
            ExternalContext = externalContext,
            FileIds = request.FileIds
                .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
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

    private static TEnum ReadContextEnum<TEnum>(
        IReadOnlyDictionary<string, object>? context,
        string key,
        TEnum fallback)
        where TEnum : struct, Enum
    {
        string? value = ReadContextValue(context, key);
        return Enum.TryParse(value, ignoreCase: true, out TEnum result)
            && Enum.IsDefined(result)
            ? result
            : fallback;
    }

    private static bool IsReservedChatContextKey(string key) =>
        key.Equals("agentId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("llmProfileId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("conversationId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("conversationType", StringComparison.OrdinalIgnoreCase)
        || key.Equals("clientType", StringComparison.OrdinalIgnoreCase)
        || key.Equals("traceId", StringComparison.OrdinalIgnoreCase);
}
