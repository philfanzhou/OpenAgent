namespace OpenAgent.Router.Models;

/// <summary>
/// 路由层特有错误的符号名（kebab-case），作为统一 ProblemDetails 契约的 code 扩展字段。
/// 与引擎侧 AgentErrorCode 派生的符号名共用同一命名风格。
/// </summary>
internal static class RouterErrorCodes
{
    internal const string AgentIdConflict = "agent-id-conflict";
    internal const string AgentNotFound = "agent-not-found";
    internal const string AgentProviderUnavailable = "agent-provider-unavailable";
    internal const string ConversationNotFound = "conversation-not-found";
    internal const string ConversationOwnerConflict = "conversation-owner-conflict";
    internal const string ConversationOwnerUnresolved = "conversation-owner-unresolved";
    internal const string ConversationProviderMismatch = "conversation-provider-mismatch";
    internal const string InvalidTenant = "invalid-tenant";
    internal const string NoAgentAvailable = "no-agent-available";
}
