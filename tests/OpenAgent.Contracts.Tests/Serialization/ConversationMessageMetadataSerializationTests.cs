using System.Text.Json;
using OpenAgent.Contracts.Conversation;
using Xunit;

namespace OpenAgent.Contracts.Tests.Serialization;

public sealed class ConversationMessageMetadataSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Metadata_WebOptions_SerializesCamelCaseWireShape()
    {
        ConversationMessageMetadata metadata = new()
        {
            Files = [new MessageFileMetadata("file-1", "notes.md", "text/markdown", 12, "files/t/f-1")],
            Reasoning = "thinking",
            ExecutionStatus = "Cancelled",
            ToolArguments = "{\"a\":1}",
            Extensions = new Dictionary<string, string> { ["Custom"] = "kept" }
        };

        string json = JsonSerializer.Serialize(metadata, JsonOptions);

        Assert.Contains("\"files\":[{\"fileId\":\"file-1\",\"fileName\":\"notes.md\",\"mediaType\":\"text/markdown\",\"length\":12,\"objectKey\":\"files/t/f-1\"}]", json, StringComparison.Ordinal);
        Assert.Contains("\"reasoning\":\"thinking\"", json, StringComparison.Ordinal);
        Assert.Contains("\"executionStatus\":\"Cancelled\"", json, StringComparison.Ordinal);
        // 字符串值内的引号由默认 JavaScriptEncoder 转义为 \u0022。
        Assert.Contains("\"toolArguments\":\"{\\u0022a\\u0022:1}\"", json, StringComparison.Ordinal);
        // 逃生舱字典键不做命名改写：第三方键原样透传。
        Assert.Contains("\"extensions\":{\"Custom\":\"kept\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_WebOptions_DeserializesPascalCasePayload()
    {
        const string json = """
            {"Files":[],"Reasoning":null,"ExecutionStatus":"Failed","ToolArguments":"{}","Extensions":{"X":"y"}}
            """;

        ConversationMessageMetadata? metadata = JsonSerializer.Deserialize<ConversationMessageMetadata>(json, JsonOptions);

        Assert.NotNull(metadata);
        Assert.Empty(metadata!.Files!);
        Assert.Null(metadata.Reasoning);
        Assert.Equal("Failed", metadata.ExecutionStatus);
        Assert.Equal("{}", metadata.ToolArguments);
        Assert.Equal("y", metadata.Extensions!["X"]);
    }

    [Fact]
    public void Message_Metadata_RoundTripsWithContextSummaryPayload()
    {
        // ContextSummary.compactedMessages 嵌套消息列表随契约整体序列化，metadata 必须随之保真。
        ConversationMessageMetadata metadata = new()
        {
            Files = [new MessageFileMetadata("file-1", "notes.md", "text/markdown", 12, null)],
            Reasoning = "thinking",
            ExecutionStatus = "Cancelled",
            ToolArguments = "{\"a\":1}",
            Extensions = new Dictionary<string, string> { ["Custom"] = "kept" }
        };
        ConversationMessage message = new()
        {
            MessageId = "message-1",
            Sequence = 3,
            Role = "assistant",
            Content = "partial",
            Metadata = metadata
        };
        ContextSummary summary = new()
        {
            CompressionId = "compression-1",
            Strategy = "Audited",
            Trigger = "Automatic",
            Status = "Succeeded",
            Summary = "s",
            LastCompressedAt = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),
            CompressedMessageCount = 1,
            OriginalStartSequence = 1,
            OriginalEndSequence = 3,
            OriginalTokenCount = 10,
            TokenCount = 4,
            SourceEndSequence = 3,
            CompactedMessages = [message]
        };

        ContextSummary? reloaded = JsonSerializer.Deserialize<ContextSummary>(
            JsonSerializer.Serialize(summary, JsonOptions), JsonOptions);

        ConversationMessage restored = Assert.Single(reloaded!.CompactedMessages);
        Assert.Equal(metadata.Files, restored.Metadata!.Files);
        Assert.Equal("thinking", restored.Metadata.Reasoning);
        Assert.Equal("Cancelled", restored.Metadata.ExecutionStatus);
        Assert.Equal("{\"a\":1}", restored.Metadata.ToolArguments);
        Assert.Equal("kept", restored.Metadata.Extensions!["Custom"]);
    }
}
