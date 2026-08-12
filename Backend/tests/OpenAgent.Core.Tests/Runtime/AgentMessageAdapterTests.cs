using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class AgentMessageAdapterTests
{
    [Fact]
    public void ToStored_ReasoningOnlyAssistantMessage_PersistsReasoningMetadata()
    {
        ChatMessage response = new(
            ChatRole.Assistant,
            [new TextReasoningContent("Inspect the uploaded file before answering.")]);
        int sequence = 1;

        ConversationMessage stored = Assert.Single(AgentMessageAdapter.ToStored([response], ref sequence));

        Assert.Equal("assistant", stored.Role);
        Assert.Equal(string.Empty, stored.Content);
        Assert.Equal("Inspect the uploaded file before answering.", stored.Metadata!["Reasoning"]);
    }

    [Fact]
    public void AssociateFiles_AssistantMessage_PersistsMetadataAndReference()
    {
        ConversationMessage message = new()
        {
            MessageId = "message-1",
            Sequence = 1,
            Role = "assistant",
            Content = "Created the requested file."
        };
        FileAsset file = new()
        {
            FileId = "generated-file-1",
            TenantId = "tenant-1",
            OwnerUserId = "user-1",
            FileName = "summary.md",
            MediaType = "text/markdown",
            Length = 12,
            Sha256 = "hash",
            ObjectKey = "files/tenant-1/generated-file-1",
            Source = FileAssetSource.Agent,
            State = FileAssetState.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        };

        ConversationMessage associated = AgentMessageAdapter.AssociateFiles(message, [file]);

        Assert.Equal([file.FileId], associated.FileIds);
        Assert.Contains(file.FileName, associated.Metadata!["Files"]);
    }

    [Fact]
    public void FromStored_ToolCall_PreservesCallIdAndToolName()
    {
        ConversationMessage stored = new()
        {
            MessageId = "message-1",
            Sequence = 1,
            Role = "assistant",
            Content = string.Empty,
            ToolCallId = "call-1",
            ToolName = "search"
        };

        ChatMessage? restored = AgentMessageAdapter.FromStored(stored);

        FunctionCallContent call = Assert.Single(
            restored!.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("search", call.Name);
    }
}
