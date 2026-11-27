namespace OpenAgent.Contracts.Conversation;

/// <summary>Routing/ownership fields only; never includes message bodies or history.</summary>
public sealed record ConversationMetadata(string UserId, string? AgentId, bool IsDeletedByUser);
