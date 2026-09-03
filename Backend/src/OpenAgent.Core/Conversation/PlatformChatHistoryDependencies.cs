using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Conversation;

internal sealed class PlatformChatHistoryDependencies(
    FileAssetExecutionContext fileExecution,
    IConversationLock conversationLock,
    ConversationSessionStore store,
    ILogger<PlatformChatHistory> logger,
    IFileAssetService fileService,
    IOptions<FileAssetOptions> fileOptions)
{
    internal FileAssetExecutionContext FileExecution { get; } = fileExecution;

    internal IConversationLock ConversationLock { get; } = conversationLock;

    internal ConversationSessionStore Store { get; } = store;

    internal ILogger<PlatformChatHistory> Logger { get; } = logger;

    internal IFileAssetService FileService { get; } = fileService;

    internal FileAssetOptions FileOptions { get; } = fileOptions.Value;
}
