using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Conversation;

internal sealed class ConversationAgentResolver(IConversationStore store)
{
    internal async Task<string?> ResolveAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.AgentId)
            || string.IsNullOrWhiteSpace(request.ConversationId)
            || string.IsNullOrWhiteSpace(user.TenantId))
        {
            return request.AgentId;
        }

        ConversationRecord? record = await store.GetRecordAsync(
            user.TenantId,
            request.ConversationId,
            cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            return null;
        }

        if (record.IsDeletedByUser
            || !string.Equals(record.UserId, user.UserId, StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                "Conversation does not belong to the current user");
        }

        return record.AgentId;
    }
}
