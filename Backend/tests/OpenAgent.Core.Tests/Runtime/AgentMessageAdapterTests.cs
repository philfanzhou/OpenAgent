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
    public void AttachFile_PdfBinary_AddsMetadataOnlyPlaceholder()
    {
        var message = new ChatMessage(ChatRole.User, "解析这个文件");

        AgentMessageAdapter.AttachFile(message, CreateAsset("report.pdf", "application/pdf", 4));

        Assert.Empty(message.Contents.OfType<DataContent>());
        TextContent placeholder = Assert.Single(
            message.Contents.OfType<TextContent>(), content => content.Text.Contains("[File:"));
        Assert.Contains("[File: report.pdf]", placeholder.Text);
        Assert.Contains("application/pdf", placeholder.Text);
        Assert.Contains("file-aware analysis tool", placeholder.Text);
        Assert.Contains("fileId=file-1", placeholder.Text);
        Assert.DoesNotContain("s3Key", placeholder.Text);
        Assert.Contains("Content is not included", placeholder.Text);
    }

    [Fact]
    public void AttachFile_Image_DoesNotInlineData()
    {
        var message = new ChatMessage(ChatRole.User, "描述这张图");

        AgentMessageAdapter.AttachFile(message, CreateAsset("chart.png", "image/png", 2));

        Assert.Empty(message.Contents.OfType<DataContent>());
        TextContent descriptor = Assert.Single(
            message.Contents.OfType<TextContent>(),
            content => content.Text.Contains("[File:", StringComparison.Ordinal));
        Assert.Contains("[File: chart.png]", descriptor.Text);
        Assert.Contains("fileId=file-1", descriptor.Text);
    }

    [Fact]
    public void AttachFile_TextFile_DoesNotInlineContent()
    {
        var message = new ChatMessage(ChatRole.User, "总结这个文件");

        AgentMessageAdapter.AttachFile(message, CreateAsset("notes.txt", "text/plain", 11));

        TextContent inlined = Assert.Single(
            message.Contents.OfType<TextContent>(), content => content.Text.Contains("[File:"));
        Assert.StartsWith("[File: notes.txt]", inlined.Text);
        Assert.Contains("fileId=file-1", inlined.Text);
        Assert.DoesNotContain("hello notes", inlined.Text);
    }

    [Fact]
    public void CreateUser_AttachedFiles_UsesMetadataOnly()
    {
        ChatMessage message = AgentMessageAdapter.CreateUser(
            "总结附件",
            [CreateAsset("notes.md", "text/markdown", 1024)]);

        Assert.Empty(message.Contents.OfType<DataContent>());
        TextContent descriptor = Assert.Single(
            message.Contents.OfType<TextContent>(),
            content => content.Text.Contains("[File:", StringComparison.Ordinal));
        Assert.Contains("fileId=file-1", descriptor.Text);
        Assert.Contains("read_file", descriptor.Text);
        Assert.DoesNotContain("#", descriptor.Text);
    }

    [Fact]
    public void CreateUser_MultimodalImage_AddsInlineDataContent()
    {
        FileAsset asset = CreateAsset("chart.png", "image/png", 2);
        ChatMessage message = AgentMessageAdapter.CreateUser(
            "Describe this image",
            [asset],
            [new FileAssetContent { Asset = asset, Data = [0x89, 0x50] }]);

        DataContent image = Assert.Single(message.Contents.OfType<DataContent>());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("chart.png", image.Name);
        TextContent descriptor = Assert.Single(
            message.Contents.OfType<TextContent>(),
            content => content.Text.Contains("[File:", StringComparison.Ordinal));
        Assert.Contains("Image content is attached", descriptor.Text);
        Assert.DoesNotContain("Content is not included", descriptor.Text);
    }

    private static FileAsset CreateAsset(string fileName, string mediaType, long length) => new()
    {
        FileId = "file-1",
        TenantId = "tenant-1",
        OwnerUserId = "user-1",
        FileName = fileName,
        MediaType = mediaType,
        Length = length,
        Sha256 = "hash",
        ObjectKey = "files/tenant-1/file-1",
        Source = FileAssetSource.UserUpload,
        State = FileAssetState.Ready,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
