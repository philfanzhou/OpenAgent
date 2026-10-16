using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using Xunit;
namespace OpenAgent.Runtime.Acp.Tests.Protocol;
public sealed class ModelTests
{
    [Theory]
    [InlineData("{\"type\":\"text\",\"text\":\"hello\"}", typeof(TextContent))]
    [InlineData("{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"image/png\"}", typeof(ImageContent))]
    [InlineData("{\"type\":\"audio\",\"data\":\"x\",\"mimeType\":\"audio/wav\"}", typeof(AudioContent))]
    [InlineData("{\"type\":\"resource_link\",\"uri\":\"file:///x\",\"name\":\"x\"}", typeof(ResourceLink))]
    [InlineData("{\"type\":\"resource\",\"resource\":{\"uri\":\"file:///x\",\"text\":\"x\"}}", typeof(EmbeddedResource))]
    public void Content_RoundTrip_AllFiveStableVariants(string json, Type expected)
    {
        ContentBlock block = AcpJson.Read<ContentBlock>(AcpJson.Parse(json)); Assert.Equal(expected, block.GetType());
        Assert.Equal(expected, AcpJson.Read<ContentBlock>(AcpJson.Element(block)).GetType());
    }
    [Theory]
    [InlineData("agent_message_chunk", "\"content\":{\"type\":\"text\",\"text\":\"hello\"}")]
    [InlineData("user_message_chunk", "\"content\":{\"type\":\"text\",\"text\":\"hello\"}")]
    [InlineData("agent_thought_chunk", "\"content\":{\"type\":\"text\",\"text\":\"hello\"}")]
    [InlineData("tool_call", "\"toolCallId\":\"t\",\"title\":\"title\"")]
    [InlineData("tool_call_update", "\"toolCallId\":\"t\"")]
    [InlineData("plan", "\"entries\":[]")]
    [InlineData("available_commands_update", "\"availableCommands\":[]")]
    [InlineData("current_mode_update", "\"currentModeId\":\"chat\"")]
    [InlineData("config_option_update", "\"configOptions\":[]")]
    [InlineData("session_info_update", "\"title\":null")]
    [InlineData("usage_update", "\"used\":1,\"size\":100")]
    [InlineData("notice", "\"severity\":\"info\",\"title\":\"test\"")]
    [InlineData("compaction_update", "\"compactionId\":\"c\",\"status\":\"completed\"")]
    [InlineData("compaction_summary_chunk", "\"compactionId\":\"c\",\"content\":{\"type\":\"text\",\"text\":\"summary\"}")]
    public void Update_RoundTrip_AllFourteenStableVariants(string tag, string fields)
    {
        SessionUpdate update = AcpJson.Read<SessionUpdate>(AcpJson.Parse("{\"sessionUpdate\":\"" + tag + "\"," + fields + "}"));
        Assert.Equal(tag, AcpJson.Element(update).GetProperty("sessionUpdate").GetString());
    }
    [Theory]
    [InlineData("null")]
    [InlineData("\"42\"")]
    [InlineData("42")]
    [InlineData("9223372036854775807")]
    public void RpcId_RoundTrip_PreservesNullStringAndInt64(string json)
    { RpcId id = AcpJson.Read<RpcId>(AcpJson.Parse(json)); Assert.Equal(json, AcpJson.Element(id).GetRawText()); }
    [Theory]
    [InlineData("true")][InlineData("1.5")][InlineData("[]")]
    public void RpcId_InvalidWireValue_IsRejected(string json) => Assert.Throws<JsonException>(() => AcpJson.Read<RpcId>(AcpJson.Parse(json)));
    [Fact]
    public void ToolPatch_ValueTypeNullRoundTripsWithoutBecomingUndefined()
    {
        ToolCallUpdate patch = AcpJson.Read<ToolCallUpdate>(AcpJson.Parse("""{"toolCallId":"t","rawInput":null,"rawOutput":{"ok":true}}"""));
        Assert.True(patch.RawInput.IsDefined); Assert.True(patch.RawInput.IsNull); Assert.False(patch.RawOutput.IsNull);
        JsonElement wire = AcpJson.Element<SessionUpdate>(patch);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("rawInput").ValueKind); Assert.True(wire.GetProperty("rawOutput").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, AcpJson.Element<SessionUpdate>(new ToolCallUpdate("t") { RawInput = Optional<JsonElement>.Null }).GetProperty("rawInput").ValueKind);
    }
    [Fact]
    public void Model_Getters_DoNotReparseJsonOrAllocate()
    {
        SessionNotification model = AcpJson.Read<SessionNotification>(AcpJson.Parse("""{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hello"}}}"""));
        AgentMessageChunk chunk = Assert.IsType<AgentMessageChunk>(model.Update); TextContent content = Assert.IsType<TextContent>(chunk.Content);
        long before = GC.GetAllocatedBytesForCurrentThread(); int length = 0;
        for (int i = 0; i < 1000; i++) length += ((TextContent)((AgentMessageChunk)model.Update).Content).Text.Length;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(5000, length); Assert.True(bytes < 1024, $"Unexpected allocations: {bytes}"); Assert.Same(chunk, model.Update); Assert.Same(content, chunk.Content);
    }
    [Fact]
    public void PatchAndUnknownFields_PreservePresenceAndOwnedJson()
    {
        ToolCallUpdate empty = new("t"), cleared = new("t") { Title = new Optional<string>(null) };
        Assert.False(AcpJson.Element(empty).TryGetProperty("title", out _)); Assert.Equal(JsonValueKind.Null, AcpJson.Element(cleared).GetProperty("title").ValueKind);
        Assert.False(AcpJson.Read<ToolCallUpdate>(AcpJson.Element(empty)).Title.IsDefined);
        Assert.True(AcpJson.Read<ToolCallUpdate>(AcpJson.Element(cleared)).Title.IsDefined);
        TextContent model;
        using (JsonDocument document = JsonDocument.Parse("""{"type":"text","text":"x","_meta":{"custom":42},"future":{"value":1}}""")) model = Assert.IsType<TextContent>(AcpJson.Read<ContentBlock>(document.RootElement));
        Assert.Equal(42, model.Meta!.Value.GetProperty("custom").GetInt32()); Assert.Equal(1, model.Extensions!["future"].GetProperty("value").GetInt32());
    }
    [Fact]
    public void Catalog_ContainsExactlyStableMethodsWithCorrectNotificationKinds()
    {
        Assert.Equal(25, AcpMethods.All.Count); Assert.Equal(25, AcpMethods.All.Select(x => x.Name).Distinct().Count());
        Assert.Equal(4, AcpMethods.All.Count(x => x.Notification)); Assert.DoesNotContain(AcpMethods.All, x => x.Name == "session/fork");
    }
}
