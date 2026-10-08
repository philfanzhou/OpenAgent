namespace OpenAgent.Core.Conversation.History;

internal interface IPlatformChatHistoryFactory
{
    PlatformChatHistory Create(PlatformChatHistoryContext context);
}
