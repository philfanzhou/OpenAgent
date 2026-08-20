using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Conversation;

public interface IConversationCompactionService
{
    Task<ContextSummary> CompactAsync(
        string tenantId,
        string conversationId,
        IAgentUserContext user,
        CancellationToken cancellationToken = default);
}
