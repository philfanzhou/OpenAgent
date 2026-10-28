using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Hosting;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class ChatRequestContextTests
{
    [Fact]
    public void ReadString_NullContext_ReturnsNull()
    {
        Assert.Null(ChatRequestContext.ReadString(
            null,
            ChatRequestContext.ConversationIdKey));
    }

    [Fact]
    public void ReadString_MissingKey_ReturnsNull()
    {
        var context = new Dictionary<string, object> { ["otherKey"] = "value" };

        Assert.Null(ChatRequestContext.ReadString(
            context,
            ChatRequestContext.ConversationIdKey));
    }

    [Theory]
    [InlineData(ChatRequestContext.ConversationIdKey, "ConversationId")]
    [InlineData(ChatRequestContext.ConversationIdKey, "CONVERSATIONID")]
    [InlineData(ChatRequestContext.AgentIdKey, "AgentId")]
    [InlineData(ChatRequestContext.AgentIdKey, "AGENTID")]
    [InlineData(ChatRequestContext.LlmProfileIdKey, "LlmProfileId")]
    [InlineData(ChatRequestContext.ConversationTypeKey, "ConversationType")]
    [InlineData(ChatRequestContext.ClientTypeKey, "ClientType")]
    [InlineData(ChatRequestContext.TraceIdKey, "TraceId")]
    public void ReadString_CaseVariantKey_ReturnsValue(string lookupKey, string storedKey)
    {
        var context = new Dictionary<string, object> { [storedKey] = "expected" };

        Assert.Equal("expected", ChatRequestContext.ReadString(context, lookupKey));
    }

    [Theory]
    [InlineData("{\"conversationId\":\"c-1\"}", "c-1")]
    [InlineData("{\"ConversationId\":\"c-2\"}", "c-2")]
    public void ReadString_JsonElementStringValue_ReturnsValue(string json, string expected)
    {
        Dictionary<string, object> context = Deserialize(json);

        string? result = ChatRequestContext.ReadString(
            context,
            ChatRequestContext.ConversationIdKey);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("{\"conversationId\":null}")]
    [InlineData("{\"ConversationId\":null}")]
    public void ReadString_JsonElementNullValue_ReturnsNull(string json)
    {
        Dictionary<string, object> context = Deserialize(json);

        Assert.Null(ChatRequestContext.ReadString(
            context,
            ChatRequestContext.ConversationIdKey));
    }

    [Fact]
    public void ReadString_JsonDocumentNullElement_ReturnsNull()
    {
        using JsonDocument document = JsonDocument.Parse("{\"conversationId\":null}");
        var context = new Dictionary<string, object>
        {
            ["conversationId"] = document.RootElement.GetProperty("conversationId").Clone()
        };

        Assert.Null(ChatRequestContext.ReadString(
            context,
            ChatRequestContext.ConversationIdKey));
    }

    [Theory]
    [InlineData("conversationId")]
    [InlineData("ConversationId")]
    public void ReadString_ClrStringValue_ReturnsValue(string storedKey)
    {
        var context = new Dictionary<string, object> { [storedKey] = "clr-value" };

        Assert.Equal("clr-value", ChatRequestContext.ReadString(
            context,
            ChatRequestContext.ConversationIdKey));
    }

    [Fact]
    public void ReadString_ClrNullValue_ReturnsNull()
    {
        var context = new Dictionary<string, object> { ["conversationId"] = null! };

        Assert.Null(ChatRequestContext.ReadString(
            context,
            ChatRequestContext.ConversationIdKey));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(true)]
    public void ReadString_ClrNonStringValue_ThrowsJsonException(object value)
    {
        var context = new Dictionary<string, object> { ["conversationId"] = value };

        JsonException exception = Assert.Throws<JsonException>(
            () => ChatRequestContext.ReadString(context, ChatRequestContext.ConversationIdKey));

        Assert.Equal(
            "The chat request context property 'conversationId' must be a string.",
            exception.Message);
    }

    [Theory]
    [InlineData("{\"conversationId\":42}")]
    [InlineData("{\"conversationId\":true}")]
    [InlineData("{\"conversationId\":[]}")]
    [InlineData("{\"conversationId\":{\"nested\":\"value\"}}")]
    public void ReadString_JsonElementNonStringValue_ThrowsJsonException(string json)
    {
        Dictionary<string, object> context = Deserialize(json);

        Assert.Throws<JsonException>(
            () => ChatRequestContext.ReadString(context, ChatRequestContext.ConversationIdKey));
    }

    [Theory]
    [InlineData("conversationId")]
    [InlineData("ConversationId")]
    [InlineData("agentId")]
    [InlineData("AGENTID")]
    [InlineData("llmProfileId")]
    [InlineData("LlmProfileId")]
    [InlineData("conversationType")]
    [InlineData("ConversationType")]
    [InlineData("clientType")]
    [InlineData("ClientType")]
    [InlineData("traceId")]
    [InlineData("TraceId")]
    public void IsReservedKey_ReservedKey_ReturnsTrue(string key)
    {
        Assert.True(ChatRequestContext.IsReservedKey(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("customKey")]
    [InlineData("conversation")]
    [InlineData("agentIdentifier")]
    [InlineData("user-id")]
    public void IsReservedKey_NonReservedKey_ReturnsFalse(string key)
    {
        Assert.False(ChatRequestContext.IsReservedKey(key));
    }

    private static Dictionary<string, object> Deserialize(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)
            ?? throw new JsonException("The test context JSON is required.");
    }
}
