using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public sealed class PlatformChatHistoryTests
{
    [Fact]
    public async Task BuildHistoryAsync_AttachesFilesToAssistantMessages()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = new()
        {
            FileId = "assistant-file",
            TenantId = "tenant-a",
            OwnerUserId = "user-a",
            FileName = "report.md",
            MediaType = "text/markdown",
            Length = 8,
            Sha256 = "sha",
            ObjectKey = $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-a")}/users/user-a/assistant-file",
            Source = FileAssetSource.Agent,
            State = FileAssetState.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        };
        repository.Assets[asset.FileId] = asset;
        repository.References.Add("conversation-a:assistant-file");
        objects.ContentsByKey[asset.ObjectKey] = "# Report"u8.ToArray();
        var service = new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128
            }));
        var history = new PlatformChatHistory(
            new ConversationContext(
                "conversation-a",
                "tenant-a",
                "user-a",
                "agent-a",
                null,
                ConversationType.User),
            "agent-a",
            "model-a",
            "continue",
            [],
            new FileAssetExecutionContext(),
            conversationLock: null!,
            store: null!,
            NullLogger<PlatformChatHistory>.Instance,
            service);
        ConversationMessage stored = ConversationSessionStore.Message(
            1,
            "assistant",
            "Here is the report.",
            fileIds: [asset.FileId]);

        IReadOnlyList<ChatMessage> restored = await history.BuildHistoryAsync(
            [stored],
            CancellationToken.None);

        ChatMessage message = Assert.Single(restored);
        Assert.Contains(
            message.Contents.OfType<TextContent>(),
            content => content.Text.Contains("# Report", StringComparison.Ordinal));
    }
}
