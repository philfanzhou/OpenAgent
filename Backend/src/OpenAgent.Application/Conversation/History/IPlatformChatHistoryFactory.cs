namespace OpenAgent.Application.Conversation.History;

internal interface IPlatformChatHistoryFactory
{
    PlatformChatHistory Create(PlatformChatHistoryContext context);
}
