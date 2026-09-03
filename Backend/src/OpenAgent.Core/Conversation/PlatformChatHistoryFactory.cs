namespace OpenAgent.Core.Conversation;

internal sealed class PlatformChatHistoryFactory(
    PlatformChatHistoryDependencies dependencies) : IPlatformChatHistoryFactory
{
    public PlatformChatHistory Create(PlatformChatHistoryContext context) =>
        new PlatformChatHistory(context, dependencies);
}
