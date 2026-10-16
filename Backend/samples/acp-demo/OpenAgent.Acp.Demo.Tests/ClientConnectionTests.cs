using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenAgent.Acp.Demo.Tests;

public sealed class ClientConnectionTests
{
    [Theory]
    [InlineData("session/request_permission", true)]
    [InlineData("fs/read_text_file", false)]
    public async Task RequestAsync_NotificationAndReverseRequestBeforeResponse_HandlesBoth(string method, bool permission)
    {
        string input = "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"early\"}}\n" +
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 9007199254740993L, method, @params = new { } }) + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"new\",\"result\":{\"sessionId\":\"early\"}}\n";
        MemoryStream output = new();
        await using JsonLineTransport transport = new(new MemoryStream(Encoding.UTF8.GetBytes(input)), output);
        ClientConnection connection = new(transport);
        List<JsonElement> notifications = [];
        JsonElement result = await connection.RequestAsync("new", "session/new", new { }, message =>
        {
            notifications.Add(message);
            return Task.CompletedTask;
        });

        Assert.Equal("early", result.GetProperty("sessionId").GetString());
        Assert.Single(notifications);
        string[] sent = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, sent.Length);
        using JsonDocument reply = JsonDocument.Parse(sent[1]);
        Assert.Equal(9007199254740993L, reply.RootElement.GetProperty("id").GetInt64());
        if (permission)
        {
            Assert.Equal("cancelled", reply.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        }
        else
        {
            Assert.Equal(-32601, reply.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [Theory]
    [InlineData(-32000)]
    [InlineData(-32601)]
    public async Task RequestAsync_RemoteError_PreservesErrorCode(int code)
    {
        string input = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "new", error = new { code, message = "Remote error" } }) + "\n";
        await using JsonLineTransport transport = new(new MemoryStream(Encoding.UTF8.GetBytes(input)), new MemoryStream());
        ClientConnection connection = new(transport);
        RpcException error = await Assert.ThrowsAsync<RpcException>(() =>
            connection.RequestAsync("new", "session/new", new { }, _ => Task.CompletedTask));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task RequestAsync_DisconnectedAgent_FailsInsteadOfReportingSuccess()
    {
        await using JsonLineTransport transport = new(new MemoryStream(), new MemoryStream());
        ClientConnection connection = new(transport);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            connection.RequestAsync("new", "session/new", new { }, _ => Task.CompletedTask));
    }
}
