using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using Xunit;

namespace OpenAgent.Infrastructure.Tests.Conversations;

public sealed class ConversationMessageMetadataJsonTests
{
    [Fact]
    public void Serialize_NullMetadata_ReturnsNull()
    {
        Assert.Null(ConversationMessageMetadataJson.Serialize(null));
    }

    [Fact]
    public void Deserialize_Whitespace_ReturnsNull()
    {
        Assert.Null(ConversationMessageMetadataJson.Deserialize("  "));
        Assert.Null(ConversationMessageMetadataJson.Deserialize(null));
    }

    [Fact]
    public void Deserialize_NewFormCamelCase_RoundTripsAllFields()
    {
        ConversationMessageMetadata source = CreateFullMetadata();
        string json = ConversationMessageMetadataJson.Serialize(source)!;

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json);

        AssertMetadataEqual(source, result);
    }

    /// <summary>旧形态：EF 默认序列化产出的 PascalCase string→string 字典。</summary>
    [Fact]
    public void Deserialize_LegacyPascalCaseDictionary_MapsKnownKeys()
    {
        const string json = """
            {"Files":"[{\"fileId\":\"file-1\",\"fileName\":\"notes.md\",\"mediaType\":\"text/markdown\",\"length\":12,\"objectKey\":\"files/t/f-1\"}]","Reasoning":"thinking","ToolArguments":"{\"a\":1}","ExecutionStatus":"Cancelled","Custom":"kept"}
            """;

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json);

        MessageFileMetadata file = Assert.Single(AssertMetadataEqual(CreateFullMetadata(), result).Files!);
        Assert.Equal("files/t/f-1", file.ObjectKey);
    }

    /// <summary>旧形态附件内层 JSON 使用 PascalCase 属性名（历史写入方不一致）。</summary>
    [Fact]
    public void Deserialize_LegacyFilesInnerJsonPascalCaseNames_IsAccepted()
    {
        const string json = """
            {"Files":"[{\"FileId\":\"file-1\",\"FileName\":\"notes.md\",\"MediaType\":\"text/markdown\",\"Length\":12,\"ObjectKey\":null}]"}
            """;

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json);

        MessageFileMetadata file = Assert.Single(result!.Files!);
        Assert.Equal("file-1", file.FileId);
        Assert.Equal("notes.md", file.FileName);
        Assert.Equal("text/markdown", file.MediaType);
        Assert.Equal(12, file.Length);
        Assert.Null(file.ObjectKey);
    }

    [Fact]
    public void Deserialize_NewFormWithUnknownKeys_KeepsThemInExtensionsVerbatim()
    {
        const string json = """
            {"files":[],"reasoning":null,"TraceId":"legacy-camel-mix","Number":"42"}
            """;

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json);

        Assert.Empty(result!.Files!);
        Assert.Null(result.Reasoning);
        Assert.Equal(2, result.Extensions!.Count);
        Assert.Equal("legacy-camel-mix", result.Extensions["TraceId"]);
        Assert.Equal("42", result.Extensions["Number"]);
    }

    [Fact]
    public void Deserialize_EmptyObject_ReturnsEmptyTypedMetadata()
    {
        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize("{}");

        Assert.NotNull(result);
        Assert.Null(result.Files);
        Assert.Null(result.Extensions);
    }

    [Fact]
    public void Deserialize_UnparsableFilesJson_PreservesRawValueInExtensionsAndWarns()
    {
        var logger = new RecordingLogger();
        const string json = """{"Files":"[not json","Reasoning":"kept"}""";

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json, logger);

        Assert.NotNull(result);
        Assert.Null(result.Files);
        Assert.Equal("[not json", result.Extensions!["Files"]);
        Assert.Equal("kept", result.Reasoning);
        LogEntry warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(5002, warning.EventId.Id);
        Assert.Contains("Files", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_UnparsableDocument_PreservesRawPayloadInExtensionsAndWarns()
    {
        var logger = new RecordingLogger();
        const string json = """{"Files":"[1,2]" """;

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json, logger);

        Assert.NotNull(result);
        Assert.Equal(json, result.Extensions![ConversationMessageMetadataJson.RawJsonExtensionKey]);
        LogEntry warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(5001, warning.EventId.Id);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"scalar\"")]
    public void Deserialize_NonObjectRoot_PreservesRawPayloadAndWarns(string json)
    {
        var logger = new RecordingLogger();

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json, logger);

        Assert.NotNull(result);
        Assert.Equal(json, result.Extensions![ConversationMessageMetadataJson.RawJsonExtensionKey]);
        Assert.Equal(5001, Assert.Single(logger.Entries).EventId.Id);
    }

    [Fact]
    public void Deserialize_NonStringScalarKnownKey_PreservesRawTextInExtensionsAndWarns()
    {
        var logger = new RecordingLogger();
        const string json = """{"reasoning":42}""";

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json, logger);

        Assert.Null(result!.Reasoning);
        Assert.Equal("42", result.Extensions!["reasoning"]);
        Assert.Equal(5002, Assert.Single(logger.Entries).EventId.Id);
    }

    [Fact]
    public void Deserialize_UnknownNonStringValue_PreservesRawJsonText()
    {
        const string json = """{"flags":[1,2,3],"nested":{"a":true}}""";

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json);

        Assert.Equal("[1,2,3]", result!.Extensions!["flags"]);
        Assert.Equal("{\"a\":true}", result.Extensions["nested"]);
    }

    [Fact]
    public void Deserialize_ExtensionsObject_MergesEntriesWithUnknownKeys()
    {
        const string json = """{"extensions":{"Known":"1"},"Other":"2"}""";

        ConversationMessageMetadata? result = ConversationMessageMetadataJson.Deserialize(json);

        Assert.Equal(2, result!.Extensions!.Count);
        Assert.Equal("1", result.Extensions["Known"]);
        Assert.Equal("2", result.Extensions["Other"]);
    }

    internal static ConversationMessageMetadata CreateFullMetadata() => new()
    {
        Files =
        [
            new MessageFileMetadata("file-1", "notes.md", "text/markdown", 12, "files/t/f-1")
        ],
        Reasoning = "thinking",
        ExecutionStatus = "Cancelled",
        ToolArguments = "{\"a\":1}",
        Extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Custom"] = "kept"
        }
    };

    internal static ConversationMessageMetadata AssertMetadataEqual(
        ConversationMessageMetadata expected,
        ConversationMessageMetadata? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Files ?? [], actual!.Files ?? []);
        Assert.Equal(expected.Reasoning, actual.Reasoning);
        Assert.Equal(expected.ExecutionStatus, actual.ExecutionStatus);
        Assert.Equal(expected.ToolArguments, actual.ToolArguments);
        Assert.Equal(
            expected.Extensions ?? new Dictionary<string, string>(),
            actual.Extensions ?? new Dictionary<string, string>());
        return actual;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);
}
