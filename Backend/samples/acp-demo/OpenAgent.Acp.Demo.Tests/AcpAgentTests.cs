using System.Text.Json;
using OpenAgent.Acp.Demo.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Acp.Demo.Tests;

public sealed class AcpAgentTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InitializeAsync_SupportedOrNewerVersion_NegotiatesV1AndCreatesSession(int version)
    {
        await using AgentHarness agent = new();
        string sessionId = await agent.InitializeSessionAsync(version);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
    }

    [Fact]
    public async Task PromptAsync_UnicodeContent_StreamsBeforeFinalResponse()
    {
        await using AgentHarness agent = new();
        string sessionId = await agent.InitializeSessionAsync();
        const string text = "你好\nACP 🥐";
        await agent.PromptAsync(sessionId, text);
        List<JsonElement> updates = [];
        JsonElement response = await agent.ReadResponseAsync("prompt", updates);

        Assert.Equal("end_turn", response.GetProperty("result").GetProperty("stopReason").GetString());
        Assert.True(updates.Count > 1);
        Assert.All(updates, update =>
        {
            Assert.Equal("session/update", update.GetProperty("method").GetString());
            Assert.Equal(sessionId, update.GetProperty("params").GetProperty("sessionId").GetString());
            Assert.False(update.TryGetProperty("id", out _));
        });
        string result = string.Concat(updates.Select(update => update.GetProperty("params").GetProperty("update")
            .GetProperty("content").GetProperty("text").GetString()));
        Assert.Equal("Echo: " + text, result);
    }

    [Fact]
    public async Task CancelAsync_ActivePrompt_ConfirmsCancellationAndAllowsNextPrompt()
    {
        await using AgentHarness agent = new();
        string sessionId = await agent.InitializeSessionAsync();
        await agent.PromptAsync(sessionId, new string('x', 100));
        Assert.Equal("session/update", (await agent.ReadAsync()).GetProperty("method").GetString());
        await agent.SendAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId } });
        JsonElement cancelled = await agent.ReadResponseAsync("prompt");
        Assert.Equal("cancelled", cancelled.GetProperty("result").GetProperty("stopReason").GetString());

        await agent.PromptAsync(sessionId, "next", "next");
        JsonElement completed = await agent.ReadResponseAsync("next");
        Assert.Equal("end_turn", completed.GetProperty("result").GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task PromptAsync_AlreadyRunning_ReturnsBusyWhileCancellationStillWorks()
    {
        await using AgentHarness agent = new();
        string sessionId = await agent.InitializeSessionAsync();
        await agent.PromptAsync(sessionId, new string('x', 100));
        await agent.ReadAsync();
        await agent.PromptAsync(sessionId, "second", "second");
        JsonElement busy = await agent.ReadResponseAsync("second");
        Assert.Equal(10001, busy.GetProperty("error").GetProperty("code").GetInt32());
        await agent.SendAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId } });
        JsonElement cancelled = await agent.ReadResponseAsync("prompt");
        Assert.Equal("cancelled", cancelled.GetProperty("result").GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task ReadAsync_InvalidJson_ReportsParseErrorAndAcceptsNextRequest()
    {
        await using AgentHarness agent = new();
        await agent.SendLineAsync("{broken json");
        JsonElement error = await agent.ReadAsync();
        Assert.Equal(-32700, error.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("id").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(await agent.InitializeSessionAsync()));
    }

    [Theory]
    [InlineData("\"version-one\"")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("65536")]
    public async Task InitializeAsync_InvalidProtocolVersion_ReturnsInvalidParams(string value)
    {
        await using AgentHarness agent = new();
        await agent.SendLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":" + value + "}}");
        JsonElement response = await agent.ReadAsync();
        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("9007199254740993")]
    [InlineData("\"string-id\"")]
    [InlineData("null")]
    public async Task RequestAsync_ValidId_EchoesOriginalIdWithoutPrecisionLoss(string id)
    {
        await using AgentHarness agent = new();
        await agent.SendLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"unknown\",\"params\":{}}");
        JsonElement response = await agent.ReadAsync();
        Assert.Equal(id, response.GetProperty("id").GetRawText());
        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task NotificationAsync_UnknownOrInvalidCancel_DoesNotProduceResponse()
    {
        await using AgentHarness agent = new();
        await agent.SendAsync(new { jsonrpc = "2.0", method = "unknown", @params = new { } });
        await agent.SendAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = 123 } });
        // Initialization must be the next response; any reply to the notifications breaks the handshake.
        Assert.False(string.IsNullOrWhiteSpace(await agent.InitializeSessionAsync()));
    }

    [Theory]
    [InlineData("session/new", "{\"cwd\":\"relative\",\"mcpServers\":[]}", -32602)]
    [InlineData("session/new", "{}", -32602)]
    [InlineData("session/prompt", "{\"sessionId\":\"missing\",\"prompt\":[]}", -32002)]
    [InlineData("session/load", "{}", -32601)]
    public async Task RequestAsync_InvalidOrUnsupportedOperation_ReturnsStructuredError(string method, string parameters, int code)
    {
        await using AgentHarness agent = new();
        await agent.InitializeSessionAsync();
        await agent.SendLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"invalid\",\"method\":\"" + method + "\",\"params\":" + parameters + "}");
        JsonElement response = await agent.ReadAsync();
        Assert.Equal(code, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(response.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task RunAsync_StdinClosesDuringPrompt_CancelsWorkAndExits()
    {
        await using AgentHarness agent = new();
        string sessionId = await agent.InitializeSessionAsync();
        await agent.PromptAsync(sessionId, new string('x', 10000));
        await agent.ReadAsync();
        // Harness disposal closes stdin and requires clean shutdown within five seconds.
    }
}
