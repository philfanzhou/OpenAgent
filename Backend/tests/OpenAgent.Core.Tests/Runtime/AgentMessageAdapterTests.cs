using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Compaction;
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
    public void ToStored_CompactionSummary_PersistsSummaryRole()
    {
        var response = new ChatMessage(ChatRole.Assistant, "Earlier conversation summary")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [CompactionMessageGroup.SummaryPropertyKey] = true
            }
        };
        int sequence = 1;

        ConversationMessage stored = Assert.Single(
            AgentMessageAdapter.ToStored([response], ref sequence));
        ChatMessage restored = Assert.IsType<ChatMessage>(AgentMessageAdapter.FromStored(stored));

        Assert.Equal("summary", stored.Role);
        Assert.StartsWith("[Conversation summary]", restored.Text);
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
        Assert.Empty(restored.Contents.OfType<TextContent>());
    }

    [Fact]
    public void FromStored_ReasoningToolCall_RestoresReasoningWithoutEmptyText()
    {
        ConversationMessage stored = new()
        {
            MessageId = "message-1",
            Sequence = 1,
            Role = "assistant",
            Content = string.Empty,
            ToolCallId = "call-1",
            ToolName = "load_skill",
            Metadata = new Dictionary<string, string>
            {
                ["Reasoning"] = "Inspect the skill first."
            }
        };

        ChatMessage? restored = AgentMessageAdapter.FromStored(stored);

        Assert.Contains(
            restored!.Contents,
            content => content is TextReasoningContent { Text: "Inspect the skill first." });
        Assert.Empty(restored.Contents.OfType<TextContent>());
    }

    [Fact]
    public void RemoveEmptyOpenAIToolCallText_EmptyText_RemovesTextFromClone()
    {
        ChatMessage message = new(
            ChatRole.Assistant,
            [
                new TextContent(string.Empty),
                new TextReasoningContent("Inspect the skill first."),
                new FunctionCallContent("call-1", "load_skill")
            ]);

        ChatMessage normalized = Assert.Single(
            AgentMessageAdapter.RemoveEmptyOpenAIToolCallText([message]));

        Assert.NotSame(message, normalized);
        Assert.Empty(normalized.Contents.OfType<TextContent>());
        Assert.Single(normalized.Contents.OfType<TextReasoningContent>());
        Assert.Single(normalized.Contents.OfType<FunctionCallContent>());
        Assert.Single(message.Contents.OfType<TextContent>());
    }

    [Fact]
    public void RemoveEmptyOpenAIToolCallText_NonEmptyText_PreservesMessage()
    {
        ChatMessage message = new(
            ChatRole.Assistant,
            [
                new TextContent("Loading the selected skill."),
                new FunctionCallContent("call-1", "load_skill")
            ]);

        ChatMessage normalized = Assert.Single(
            AgentMessageAdapter.RemoveEmptyOpenAIToolCallText([message]));

        Assert.Same(message, normalized);
    }

    [Fact]
    public void AttachFile_PdfBinary_AddsTextPlaceholderInsteadOfDataContent()
    {
        var message = new ChatMessage(ChatRole.User, "解析这个文件");

        AgentMessageAdapter.AttachFile(message, CreateContent("report.pdf", "application/pdf", [0x25, 0x50, 0x44, 0x46]));

        Assert.Empty(message.Contents.OfType<DataContent>());
        TextContent placeholder = Assert.Single(
            message.Contents.OfType<TextContent>(), content => content.Text.Contains("[File:"));
        Assert.Contains("[File: report.pdf]", placeholder.Text);
        Assert.Contains("application/pdf", placeholder.Text);
        Assert.Contains("read_file", placeholder.Text);
        Assert.Contains("fileId=file-1", placeholder.Text);
        Assert.Contains("s3Key=files/tenant-1/file-1", placeholder.Text);
        Assert.Contains("s3Key", placeholder.Text);
    }

    [Fact]
    public void AttachFile_Image_AddsDataContent()
    {
        var message = new ChatMessage(ChatRole.User, "描述这张图");

        AgentMessageAdapter.AttachFile(message, CreateContent("chart.png", "image/png", [0x89, 0x50]));

        DataContent data = Assert.Single(message.Contents.OfType<DataContent>());
        Assert.Equal("image/png", data.MediaType);
        Assert.Single(message.Contents.OfType<TextContent>(), content => !string.IsNullOrEmpty(content.Text));
        Assert.DoesNotContain(message.Contents.OfType<TextContent>(), content => content.Text.Contains("[File:"));
    }

    [Fact]
    public void AttachFile_TextFile_InlinesUtf8Content()
    {
        var message = new ChatMessage(ChatRole.User, "总结这个文件");

        AgentMessageAdapter.AttachFile(
            message, CreateContent("notes.txt", "text/plain", "hello notes"u8.ToArray()));

        TextContent inlined = Assert.Single(
            message.Contents.OfType<TextContent>(), content => content.Text.Contains("[File:"));
        Assert.StartsWith("[File: notes.txt]", inlined.Text);
        Assert.Contains("hello notes", inlined.Text);
    }

    private static FileAssetContent CreateContent(string fileName, string mediaType, byte[] data) => new()
    {
        Asset = new FileAsset
        {
            FileId = "file-1",
            TenantId = "tenant-1",
            OwnerUserId = "user-1",
            FileName = fileName,
            MediaType = mediaType,
            Length = data.Length,
            Sha256 = "hash",
            ObjectKey = "files/tenant-1/file-1",
            Source = FileAssetSource.UserUpload,
            State = FileAssetState.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        },
        Data = data
    };
}
