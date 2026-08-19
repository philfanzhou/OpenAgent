using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Conversation;

/// <summary>Platform coordinates for loading and persisting one conversation.</summary>
internal readonly record struct ConversationContext(
    string? ConversationId,
    string? TenantId,
    string? UserId,
    string? AgentId,
    string? TraceId,
    ConversationType Type,
    ConversationOwnerRole OwnerRole)
{
    internal bool IsValid =>
        !string.IsNullOrEmpty(ConversationId)
        && !string.IsNullOrEmpty(TenantId);
}
