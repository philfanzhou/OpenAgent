using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Conversation.History;

internal sealed record PlatformChatHistoryContext(
    ConversationContext Conversation,
    string ModelId,
    string Input,
    IReadOnlyList<FileAsset> Files,
    bool SupportsMultimodal);
