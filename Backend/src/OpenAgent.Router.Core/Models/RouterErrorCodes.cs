namespace OpenAgent.Router.Models;

internal static class RouterErrorCodes
{
    internal const string AgentIdConflict = "agent_id_conflict";
    internal const string AgentNotFound = "agent_not_found";
    internal const string AgentProviderUnavailable = "agent_provider_unavailable";
    internal const string ConversationNotFound = "conversation_not_found";
    internal const string ConversationOwnerConflict = "conversation_owner_conflict";
    internal const string ConversationOwnerUnresolved = "conversation_owner_unresolved";
    internal const string ConversationProviderMismatch = "conversation_provider_mismatch";
    internal const string InvalidTenant = "invalid_tenant";
    internal const string NoAgentAvailable = "no_agent_available";
}
