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
            content => content.Text.Contains("fileId=assistant-file", StringComparison.Ordinal));
        Assert.DoesNotContain(
            message.Contents.OfType<TextContent>(),
            content => content.Text.Contains("# Report", StringComparison.Ordinal));
        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task BuildHistoryAsync_MultimodalModel_InlinesReferencedImage()
    {
        RecordingFileAssetRepository repository = new();
        RecordingFileObjectStore objects = new();
        FileAsset asset = new()
        {
            FileId = "assistant-image",
            TenantId = "tenant-a",
            OwnerUserId = "user-a",
            FileName = "chart.png",
            MediaType = "image/png",
            Length = 2,
            Sha256 = "sha",
            ObjectKey = $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-a")}/users/user-a/assistant-image",
            Source = FileAssetSource.Agent,
            State = FileAssetState.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        };
        repository.Assets[asset.FileId] = asset;
        repository.References.Add("conversation-a:assistant-image");
        objects.ContentsByKey[asset.ObjectKey] = [0x89, 0x50];
        FileAssetService service = new(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128,
                MaxInlineImageBytes = 16,
                MaxInlineImageCount = 1
            }));
        PlatformChatHistory history = new(
            new ConversationContext("conversation-a", "tenant-a", "user-a", "agent-a", null, ConversationType.User),
            "agent-a",
            "model-a",
            "continue",
            [],
            new FileAssetExecutionContext(),
            conversationLock: null!,
            store: null!,
            NullLogger<PlatformChatHistory>.Instance,
            service,
            supportsMultimodal: true,
            maxInlineImageBytes: 16,
            maxInlineImageCount: 1);

        ConversationMessage stored = ConversationSessionStore.Message(
            1,
            "assistant",
            "Here is the image.",
            fileIds: [asset.FileId]);

        IReadOnlyList<ChatMessage> restored = await history.BuildHistoryAsync(
            [stored],
            CancellationToken.None);

        ChatMessage message = Assert.Single(restored);
        DataContent image = Assert.Single(message.Contents.OfType<DataContent>());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(1, objects.ReadCount);
    }
}
