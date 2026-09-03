namespace OpenAgent.Core.Conversation;

internal interface IPlatformChatHistoryFactory
{
    PlatformChatHistory Create(PlatformChatHistoryContext context);
}
