using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Core.Runtime.Agent;
using OpenAI;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class OpenAIChatMessageSerializationTests
{
    [Fact]
    public async Task GetResponseAsync_NormalizedToolCallMessage_OmitsEmptyContent()
    {
        CaptureHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri("http://localhost/v1"),
            Transport = new HttpClientPipelineTransport(httpClient)
        };
        IChatClient client = new OpenAIClient(new ApiKeyCredential("test-key"), options)
            .GetChatClient("test-model")
            .AsIChatClient();
        ChatMessage assistant = new(
            ChatRole.Assistant,
            [
                new TextContent(string.Empty),
                new FunctionCallContent("call-1", "load_skill")
            ]);
        ChatMessage tool = new(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "loaded")]);
        IEnumerable<ChatMessage> messages = AgentMessageAdapter.RemoveEmptyOpenAIToolCallText(
            [new ChatMessage(ChatRole.User, "Load the skill."), assistant, tool]);

        await client.GetResponseAsync(messages);

        using JsonDocument payload = JsonDocument.Parse(handler.RequestBody!);
        JsonElement serializedAssistant = payload.RootElement
            .GetProperty("messages")[1];
        Assert.False(serializedAssistant.TryGetProperty("content", out _));
        Assert.True(serializedAssistant.TryGetProperty("tool_calls", out _));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id": "chatcmpl-test",
                      "object": "chat.completion",
                      "created": 1,
                      "model": "test-model",
                      "choices": [
                        {
                          "index": 0,
                          "message": { "role": "assistant", "content": "ok" },
                          "finish_reason": "stop"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
