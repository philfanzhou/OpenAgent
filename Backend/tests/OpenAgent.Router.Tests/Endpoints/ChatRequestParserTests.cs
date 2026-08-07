using Microsoft.AspNetCore.Http;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ChatRequestParserTests
{
    [Fact]
    public void Parse_ChatContract_ReadsMessageAndContext()
    {
        const string body = """
            {
              "message": "find the invoice",
              "context": {
                "conversationId": "conversation-1",
                "agentId": "finance"
              }
            }
            """;

        (string query, string? conversationId, string? agentId) = ChatRequestParser.Parse(body);

        Assert.Equal("find the invoice", query);
        Assert.Equal("conversation-1", conversationId);
        Assert.Equal("finance", agentId);
    }

    [Fact]
    public void Parse_LegacyContract_ReadsDirectProperties()
    {
        const string body = """
            {
              "query": "run workflow",
              "conversationId": "conversation-2",
              "agentId": "operations"
            }
            """;

        (string query, string? conversationId, string? agentId) = ChatRequestParser.Parse(body);

        Assert.Equal("run workflow", query);
        Assert.Equal("conversation-2", conversationId);
        Assert.Equal("operations", agentId);
    }

    [Fact]
    public async Task ReadAsync_MultipartForm_ReadsFieldsAndRewindsBody()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("inspect attachment"), "message");
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

        Assert.Equal("inspect attachment", request.Query);
        Assert.Equal("conversation-3", request.ConversationId);
        Assert.Equal("documents", request.AgentId);
        Assert.Equal(0, body.Position);
    }
}
