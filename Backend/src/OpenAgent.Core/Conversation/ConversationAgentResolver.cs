using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Configuration;
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
        ConversationResolution resolution = await ResolveContextAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        return resolution.AgentId;
    }

    internal async Task<ConversationResolution> ResolveContextAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationId)
            || string.IsNullOrWhiteSpace(user.TenantId))
        {
            return new ConversationResolution(request.AgentId, null);
        }

        ConversationRecord? record = await store.GetRecordAsync(
            user.TenantId,
            request.ConversationId,
            cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            return new ConversationResolution(request.AgentId, null);
        }

        if (record.IsDeletedByUser
            || record.Type != request.ConversationType
            || !string.Equals(record.UserId, user.UserId, StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                "Conversation does not belong to the current user");
        }

        return new ConversationResolution(
            string.IsNullOrWhiteSpace(request.AgentId) ? record.AgentId : request.AgentId,
            record.ModelOverride);
    }
}

internal sealed record ConversationResolution(
    string? AgentId,
    LlmModelSelection? ModelOverride);
