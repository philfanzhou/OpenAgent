using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Application.Conversation.Store;

namespace OpenAgent.Application.Conversation.History;

internal sealed class ConversationHistoryFactory(
    ConversationSessionStore store,
    IServiceProvider services)
{
    internal PlatformChatHistory Create(
        TurnContext turn,
        string modelId,
        string input,
        IReadOnlyList<FileAsset> files,
        bool supportsMultimodal)
    {
        ConversationContext context = turn.ToConversationContext();
        return ActivatorUtilities.CreateInstance<PlatformChatHistory>(services, new PlatformChatHistoryContext(
            context,
            modelId,
            input,
            files.ToList().AsReadOnly(),
            supportsMultimodal));
    }

    internal async Task EnsureConversationAsync(
        TurnContext turn,
        string input,
        CancellationToken cancellationToken)
    {
        ConversationContext context = turn.ToConversationContext();
        await store.OpenAsync(
            context,
            turn.AgentId ?? string.Empty,
            input,
            cancellationToken).ConfigureAwait(false);
    }
}
