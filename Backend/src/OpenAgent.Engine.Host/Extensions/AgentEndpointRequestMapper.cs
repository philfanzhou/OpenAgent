using System.Text.Json;
using OpenAgent.Contracts.Configuration;
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
        string? modelScope = ReadContextValue(request.Context, "modelScope");
        LlmModelSelection? modelSelection = ReadModelSelection(request.Context, modelScope);
        bool updateConversationModel = string.Equals(
            modelScope,
            "conversation",
            StringComparison.OrdinalIgnoreCase);
        return new AgentRequest
        {
            Query = request.Message,
            AgentId = ReadContextValue(request.Context, "agentId")
                ?? context.Request.Headers["X-Agent-Id"].FirstOrDefault(),
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
            ConversationModelOverride = updateConversationModel ? modelSelection : null,
            UpdateConversationModelOverride = updateConversationModel,
            MessageModelOverride = string.Equals(
                modelScope,
                "message",
                StringComparison.OrdinalIgnoreCase)
                ? modelSelection
                : null,
            ExternalContext = externalContext,
            FileIds = request.FileIds
                .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static LlmModelSelection? ReadModelSelection(
        IReadOnlyDictionary<string, object>? context,
        string? scope)
    {
        string? provider = ReadContextValue(context, "modelProvider");
        string? modelId = ReadContextValue(context, "modelId");
        bool hasSelection = !string.IsNullOrWhiteSpace(provider)
            || !string.IsNullOrWhiteSpace(modelId);
        if (string.IsNullOrWhiteSpace(scope))
        {
            if (hasSelection)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    "modelScope is required when a model override is provided.");
            }
            return null;
        }

        if (!scope.Equals("conversation", StringComparison.OrdinalIgnoreCase)
            && !scope.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "modelScope must be either 'conversation' or 'message'.");
        }

        if (!hasSelection && scope.Equals("conversation", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(modelId))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "A model override requires both modelProvider and modelId.");
        }

        return new LlmModelSelection
        {
            Provider = provider,
            ModelId = modelId
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
        || key.Equals("conversationId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("conversationType", StringComparison.OrdinalIgnoreCase)
        || key.Equals("clientType", StringComparison.OrdinalIgnoreCase)
        || key.Equals("traceId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("modelScope", StringComparison.OrdinalIgnoreCase)
        || key.Equals("modelProvider", StringComparison.OrdinalIgnoreCase)
        || key.Equals("modelId", StringComparison.OrdinalIgnoreCase);
}
