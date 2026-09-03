using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Conversation;

public interface IConversationCompactionService
{
    Task<ContextSummary> CompactAsync(
        string tenantId,
        string conversationId,
        string llmProfileId,
        IAgentUserContext user,
        CancellationToken cancellationToken = default);
}
