using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Core.Tests.TestDoubles;

/// <summary>
/// 测试装配用的 TurnContext 工厂：收敛各测试里逐字段拼装的样板，
/// 与生产路径一致的默认值（tenant/user/conversation/trace/agent）。
/// </summary>
internal static class TurnContexts
{
    internal static TurnContext Create(
        string tenantId = "tenant",
        string userId = "user",
        string? conversationId = "conversation",
        string traceId = "trace",
        string? agentId = "agent",
        ConversationType? conversationType = ConversationType.User) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        ConversationId = conversationId,
        TraceId = traceId,
        AgentId = agentId,
        ConversationType = conversationType
    };
}
