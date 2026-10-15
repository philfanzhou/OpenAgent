using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Application.Conversation;

namespace OpenAgent.Application.Conversation.History;

internal static class TurnConversationExtensions
{
    internal static ConversationContext ToConversationContext(this TurnContext turn) => new(
        turn.ConversationId,
        turn.TenantId,
        turn.UserId,
        turn.AgentId,
        turn.TraceId,
        turn.ConversationType ?? ConversationType.User);
}
