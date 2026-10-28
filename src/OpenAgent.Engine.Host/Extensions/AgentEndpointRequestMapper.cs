using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting;
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
            ? ChatRequestContext.ReadString(request.Context, ChatRequestContext.ConversationIdKey)
                ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault()
                ?? Guid.NewGuid().ToString()
            : null;
        Dictionary<string, string>? externalContext = request.Context?
            .Where(item => !ChatRequestContext.IsReservedKey(item.Key))
            .ToDictionary(item => item.Key, item => item.Value?.ToString() ?? string.Empty);
        return new AgentRequest
        {
            Query = request.Message,
            AgentId = ChatRequestContext.ReadString(request.Context, ChatRequestContext.AgentIdKey)
                ?? context.Request.Headers["X-Agent-Id"].FirstOrDefault(),
            LlmProfileId = ChatRequestContext.ReadString(request.Context, ChatRequestContext.LlmProfileIdKey)
                ?? context.Request.Headers["X-OpenAgent-Llm-Profile-Id"].FirstOrDefault(),
            ConversationId = conversationId,
            ConversationType = ReadContextEnum(
                request.Context,
                ChatRequestContext.ConversationTypeKey,
                ConversationType.User),
            TraceId = feature.TraceId,
            ClientType = ReadContextEnum(
                request.Context,
                ChatRequestContext.ClientTypeKey,
                ClientType.Web),
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

    private static TEnum ReadContextEnum<TEnum>(
        Dictionary<string, object>? context,
        string key,
        TEnum fallback)
        where TEnum : struct, Enum
    {
        string? value = ChatRequestContext.ReadString(context, key);
        return Enum.TryParse(value, ignoreCase: true, out TEnum result)
            && Enum.IsDefined(result)
            ? result
            : fallback;
    }
}
