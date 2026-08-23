using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ChatRequestReaderTests
{
    [Fact]
    public async Task ReadAsync_JsonChatContract_ReadsContextAndRewindsBody()
    {
        DefaultHttpContext context = CreateJsonContext("""
            {
              "Message": "find the invoice",
              "Context": {
                "ConversationId": "conversation-1",
                "AgentId": "finance"
              }
            }
            """);

        ParsedChatRequest request = await ChatRequestReader.ReadAsync(
            context.Request,
            CancellationToken.None);

        Assert.Equal("find the invoice", request.Query);
        Assert.Equal("conversation-1", request.ConversationId);
        Assert.Equal("finance", request.AgentId);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"message\":{}}")]
    [InlineData("{\"context\":[]}")]
    [InlineData("{\"context\":{\"agentId\":42}}")]
    public async Task ReadAsync_InvalidJsonShape_ThrowsJsonException(string body)
    {
        DefaultHttpContext context = CreateJsonContext(body);

        await Assert.ThrowsAsync<JsonException>(() => ChatRequestReader.ReadAsync(
            context.Request,
            CancellationToken.None));

        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task ReadAsync_MultipartForm_ReadsFieldsAndRewindsBody()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("inspect file"), "message");
        form.Add(new StringContent("conversation-3"), "conversationId");
        form.Add(new StringContent("documents"), "agentId");
        await using var body = new MemoryStream();
        await form.CopyToAsync(body);
        body.Position = 0;
        var context = new DefaultHttpContext();
        context.Request.ContentType = form.Headers.ContentType!.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = body;

        ParsedChatRequest request = await ChatRequestReader.ReadAsync(
            context.Request,
            CancellationToken.None);

        Assert.Equal("inspect file", request.Query);
        Assert.Equal("conversation-3", request.ConversationId);
        Assert.Equal("documents", request.AgentId);
        Assert.Equal(0, body.Position);
    }

    [Fact]
    public async Task ReadAsync_CachedBodySnapshot_DoesNotReadRequestStreamAgain()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"message\":\"hello\"}");
        var stream = new CountingStream(body);
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = stream;
        await RequestBodySnapshot.GetAsync(context, 1024);
        int readsAfterSnapshot = stream.ReadCount;

        ParsedChatRequest request = await ChatRequestReader.ReadAsync(
            context.Request,
            CancellationToken.None);

        Assert.Equal("hello", request.Query);
        Assert.Equal(readsAfterSnapshot, stream.ReadCount);
    }

    private static DefaultHttpContext CreateJsonContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        return context;
    }

    private sealed class CountingStream(byte[] buffer) : MemoryStream(buffer)
    {
        internal int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(destination, cancellationToken);
        }
    }
}
