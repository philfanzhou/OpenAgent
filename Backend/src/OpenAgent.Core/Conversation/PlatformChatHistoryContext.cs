using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Conversation;

internal sealed record PlatformChatHistoryContext(
    ConversationContext Conversation,
    string ModelId,
    string Input,
    IReadOnlyList<FileAsset> Files,
    bool SupportsMultimodal,
    IReadOnlyDictionary<string, string>? ExecutionMetadata = null);
