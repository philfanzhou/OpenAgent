using Microsoft.Extensions.DependencyInjection;

namespace OpenAgent.Core.Conversation.History;

internal sealed class PlatformChatHistoryFactory(
    IServiceProvider services) : IPlatformChatHistoryFactory
{
    public PlatformChatHistory Create(PlatformChatHistoryContext context) =>
        ActivatorUtilities.CreateInstance<PlatformChatHistory>(services, context);
}
