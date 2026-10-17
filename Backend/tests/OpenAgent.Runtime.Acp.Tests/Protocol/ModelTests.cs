using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Protocol;

public sealed class ModelTests
{
    [Fact]
    public void Optional_Patches_PreserveOmittedNullAndEmpty()
    {
        ToolCallUpdate omitted = new("tool-1");
        ToolCallUpdate cleared = new("tool-1", content: Optional<ToolCallContent[]>.Null);
        ToolCallUpdate empty = new("tool-1", content: Array.Empty<ToolCallContent>());
        Assert.False(omitted.Content.IsDefined);
        Assert.True(cleared.Content.IsNull);
        Assert.Empty(empty.Content.Value);
        Assert.False(AcpJson.Deserialize<ToolCallUpdate>(AcpJson.Serialize(omitted)).Content.IsDefined);
        Assert.True(AcpJson.Deserialize<ToolCallUpdate>(AcpJson.Serialize(cleared)).Content.IsNull);
        Assert.Empty(AcpJson.Deserialize<ToolCallUpdate>(AcpJson.Serialize(empty)).Content.Value);
        Assert.Throws<InvalidOperationException>(() => omitted.Content.Value);
        SessionInfoUpdate info = new(title: Optional<string>.Null);
        Assert.True(AcpJson.Deserialize<SessionInfoUpdate>(AcpJson.Serialize(info)).Title.IsNull);
        CompactionUpdate compaction = new("comp-1", "completed", summary: Array.Empty<ContentBlock>(), error: Optional<string>.Null);
        Assert.Empty(compaction.Summary.Value);
        Assert.True(compaction.Error.IsNull);
    }

    [Fact]
    public void Defaults_MissingAndInvalidCapabilities_UseDeclaredDefaults()
    {
        InitializeRequest missing = AcpJson.Deserialize<InitializeRequest>("{\"protocolVersion\":1}");
        Assert.False(missing.ClientCapabilities.IsDefined);
        Assert.False(missing.ClientCapabilitiesOrDefault.TerminalOrDefault);
        Assert.False(missing.ClientCapabilitiesOrDefault.FsOrDefault.ReadTextFileOrDefault);
        InitializeRequest invalid = AcpJson.Deserialize<InitializeRequest>("{\"protocolVersion\":1,\"clientCapabilities\":null}");
        Assert.False(invalid.ClientCapabilities.IsDefined);
        Assert.False(invalid.ClientCapabilitiesOrDefault.TerminalOrDefault);
        InitializeResponse response = AcpJson.Deserialize<InitializeResponse>("{\"protocolVersion\":1,\"authMethods\":42}");
        Assert.Empty(response.AuthMethodsOrDefault);
        Assert.False(response.AgentCapabilitiesOrDefault.LoadSessionOrDefault);
        ElicitationSchema form = new();
        Assert.Equal("object", form.TypeOrDefault);
        Assert.Empty(form.PropertiesOrDefault);
    }

    [Fact]
    public void EmptyCapabilityObjects_AreDifferentFromOmittedOrNull()
    {
        ClientCapabilities capabilities = AcpJson.Deserialize<ClientCapabilities>("""
            {"elicitation":{"form":{},"url":null},"session":{"configOptions":{"boolean":{}},"notices":{},"compaction":{}}}
            """);
        Assert.True(capabilities.Elicitation.Value.Form.HasValue);
        Assert.True(capabilities.Elicitation.Value.Url.IsNull);
        Assert.True(capabilities.Session.Value.ConfigOptions.Value.Boolean.HasValue);
        Assert.True(capabilities.Session.Value.Notices.HasValue);
        Assert.True(capabilities.Session.Value.Compaction.HasValue);
        Assert.False(new ElicitationCapabilities().Form.IsDefined);
        Assert.False(new ElicitationCapabilities().Url.IsDefined);
    }

    [Fact]
    public void Compatibility_OptInFieldsRecoverAndInvalidItemsAreSkipped()
    {
        ToolCallUpdate update = AcpJson.Deserialize<ToolCallUpdate>("""
            {"toolCallId":"tool-1","kind":"future-kind","title":42,"content":[
                {"type":"content","content":{"type":"text","text":"ok"}},
                {"type":"diff","path":"/x"},null, {"type":"future"}],"locations":[]}
            """);
        Assert.True(update.Kind.IsNull);
        Assert.True(update.Title.IsNull);
        Assert.Single(update.Content.Value);
        Assert.Empty(update.Locations.Value);
        Assert.Equal("ok", update.Content.Value[0].AsContent().Content.AsText().Text);
        Assert.Throws<JsonException>(() => new ToolCallUpdate("tool-1", kind: "future-kind"));
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<PromptRequest>("""
            {"sessionId":"s","prompt":[{"type":"text","text":42}]}
            """));
    }

    [Theory]
    [InlineData("ContentBlock", "{\"type\":\"text\"}")]
    [InlineData("ContentBlock", "{\"type\":\"image\",\"data\":3,\"mimeType\":\"image/png\"}")]
    [InlineData("ContentBlock", "{\"type\":\"Text\",\"text\":\"x\"}")]
    [InlineData("SessionUpdate", "{\"sessionUpdate\":\"agent_message_chunk\"}")]
    [InlineData("SessionUpdate", "{\"sessionUpdate\":\"unknown\"}")]
    [InlineData("RequestPermissionOutcome", "{\"outcome\":\"selected\"}")]
    [InlineData("AuthMethod", "{\"type\":\"terminal\",\"id\":\"x\"}")]
    [InlineData("McpServer", "{\"type\":\"http\",\"name\":\"x\",\"command\":\"x\",\"args\":[],\"env\":[]}")]
    [InlineData("SetSessionConfigOptionRequest", "{\"sessionId\":\"s\",\"configId\":\"c\",\"type\":\"boolean\",\"value\":\"string\"}")]
    [InlineData("CreateElicitationResponse", "{\"action\":\"accept\",\"content\":[]}")]
    [InlineData("ElicitationPropertySchema", "{\"type\":\"array\"}")]
    public void Union_KnownMalformedPayload_DoesNotFallBack(string schema, string json)
        => Assert.Throws<JsonException>(() => AcpProtocol.Schemas[schema].Deserialize(json));

    [Fact]
    public void AuthMethod_UnknownType_IsRejectedInsteadOfDegradingToAgent()
    {
        // PROTOCOL.md §5: a missing type is the agent method; any other discriminator value
        // is rejected rather than silently dropped and downgraded to the agent variant.
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<AuthMethod>("""{"type":"bogus","id":"x","name":"n"}"""));
        AuthMethod agent = AcpJson.Deserialize<AuthMethod>("""{"type":"agent","id":"x","name":"n"}""");
        Assert.Equal("x", agent.AsAgent().Id);
        Assert.Equal("agent", agent.Json.GetProperty("type").GetString());
        AuthMethod omitted = AcpJson.Deserialize<AuthMethod>("""{"id":"x","name":"n"}""");
        Assert.Equal("x", omitted.AsAgent().Id);
        Assert.False(omitted.Json.TryGetProperty("type", out _));
    }

    [Fact]
    public void Unions_TypedConstructionAndProjection_AreDiscriminated()
    {
        ContentBlock text = new(new ContentBlockText("hello"));
        SessionUpdate update = new(new SessionUpdateAgentMessageChunk(text));
        SessionNotification notification = new("session-1", update);
        SessionNotification decoded = AcpJson.Deserialize<SessionNotification>(AcpJson.Serialize(notification));
        Assert.Equal("hello", decoded.Update.AsAgentMessageChunk().Content.AsText().Text);
        Assert.False(decoded.Update.AsAgentMessageChunk().MessageId.IsDefined);
        Assert.Throws<JsonException>(() => decoded.Update.AsToolCall());
        AuthMethod terminal = AcpJson.Deserialize<AuthMethod>("{\"type\":\"terminal\",\"id\":\"login\",\"name\":\"Login\"}");
        Assert.Equal("login", terminal.AsTerminal().Id);
        Assert.Throws<JsonException>(() => terminal.AsAgent());
        AuthMethod agent = AcpJson.Deserialize<AuthMethod>("{\"id\":\"login\",\"name\":\"Login\"}");
        Assert.Equal("login", agent.AsAgent().Id);
        McpServer stdio = AcpJson.Deserialize<McpServer>("{\"name\":\"test\",\"command\":\"unused\",\"args\":[],\"env\":[]}");
        Assert.Equal("unused", stdio.AsStdio().Command);
    }

    [Fact]
    public void OpenVariantsAndUnknownFields_ReadWithoutInventingKnownActions()
    {
        Notice notice = new("future-severity", "title");
        Assert.Equal("future-severity", notice.Severity);
        TextContent text = AcpJson.Deserialize<TextContent>("{\"text\":\"x\",\"future\":42,\"_meta\":{\"custom\":42}}");
        Assert.False(text.Json.TryGetProperty("future", out _));
        Assert.Equal(42, text.Meta.Value.GetProperty("custom").GetInt32());
        CreateElicitationResponse response = AcpJson.Deserialize<CreateElicitationResponse>("{\"action\":\"_defer\",\"future\":42}");
        Assert.Equal(42, response.Json.GetProperty("future").GetInt32());
        Assert.Throws<JsonException>(() => response.AsAccept());
        CreateElicitationResponse accepted = AcpJson.Deserialize<CreateElicitationResponse>("{\"action\":\"accept\",\"content\":{\"answer\":true}}");
        Assert.Throws<JsonException>(() => accepted.AsOther());
        SetSessionConfigOptionRequest config = AcpJson.Deserialize<SetSessionConfigOptionRequest>("{\"sessionId\":\"s\",\"configId\":\"c\",\"type\":\"future\",\"value\":\"model-1\"}");
        Assert.Equal("model-1", config.AsValueId().Value);
        Assert.False(config.Json.TryGetProperty("type", out _));
        Assert.Throws<JsonException>(() => AcpProtocol.Schemas["TextContent"].ValidateForSend(AcpJson.ParseElement("{\"text\":\"x\",\"future\":42}")));
    }

    [Fact]
    public void JsonOwnership_InputDocumentCanBeDisposedAndCollectionsCannotMutateModel()
    {
        TextContent model;
        Error error;
        using (JsonDocument input = JsonDocument.Parse("{\"text\":\"hello\",\"_meta\":{\"nested\":[1,{\"x\":\"retained\"}]}}"))
        {
            model = AcpJson.FromElement<TextContent>(input.RootElement);
            error = new Error(-32002, "Missing", input.RootElement.GetProperty("_meta"));
        }
        Assert.Equal("retained", model.Meta.Value.GetProperty("nested")[1].GetProperty("x").GetString());
        Assert.Equal(1, error.Data.Value.GetProperty("nested")[0].GetInt32());
        AcpRpcException exception = new(error);
        Assert.Equal(-32002, exception.Code);
        Assert.Same(error, exception.Error);
        Assert.Contains("nested", AcpJson.Serialize(exception.Error));
        Plan plan = new([new PlanEntry("original", "high", "pending")]);
        plan.Entries[0] = new PlanEntry("mutated", "low", "completed");
        Assert.Equal("original", plan.Entries[0].Content);
    }

    [Theory]
    [InlineData("{\"protocolVersion\":1,\"protocolVersion\":2}")]
    [InlineData("{\"ProtocolVersion\":1}")]
    [InlineData("{\"protocolVersion\":null}")]
    [InlineData("{\"protocolVersion\":65536}")]
    [InlineData("{\"protocolVersion\":-1}")]
    [InlineData("{\"protocolVersion\":1.0}")]
    public void RequiredFields_CaseNullDuplicatesAndRange_AreRejected(string json)
        => Assert.Throws<JsonException>(() => AcpJson.Deserialize<InitializeRequest>(json));

    [Fact]
    public void ResponseEnvelope_ResultAndError_AreMutuallyExclusive()
    {
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<AgentResponse>(
            """{"id":1,"result":{"protocolVersion":1},"error":{"code":-32603,"message":"m"}}"""));
    }

    [Fact]
    public void Optional_UndefinedJsonElement_IsOmittedNotACrashingValue()
    {
        TextContent content = new("x", meta: default(JsonElement));
        Assert.False(content.Meta.IsDefined);
        string json = AcpJson.Serialize(content);
        Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("_meta", out _));
    }

    [Fact]
    public void AbstractAcpModel_DirectReflectionSerialization_IsRejectedLikeAcpJson()
    {
        TextContent content = new("x");
        NotSupportedException written = Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize(content, typeof(AcpModel)));
        Assert.Contains("AcpJson", written.Message);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("""{"text":"x"}""", typeof(AcpModel)));
    }

    [Fact]
    public void Numbers_Unsigned64AndSigned64_AreExact()
    {
        UsageUpdate usage = new(ulong.MaxValue, 9007199254740993UL);
        UsageUpdate decoded = AcpJson.Deserialize<UsageUpdate>(AcpJson.Serialize(usage));
        Assert.Equal(ulong.MaxValue, decoded.Used);
        Assert.Equal(9007199254740993UL, decoded.Size);
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<UsageUpdate>("{\"used\":18446744073709551616,\"size\":0}"));
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<UsageUpdate>("{\"used\":-1,\"size\":0}"));
        Assert.Throws<JsonException>(() => AcpProtocol.Schemas["ErrorCode"].Deserialize("2147483648"));
        Assert.Equal(long.MaxValue, AcpProtocol.Schemas["ElicitationContentValue"].Deserialize(long.MaxValue.ToString()) is AcpModel value ? value.Json.GetInt64() : 0);
    }
}
