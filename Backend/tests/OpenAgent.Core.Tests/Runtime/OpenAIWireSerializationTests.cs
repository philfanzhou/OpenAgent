using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Runtime.Agent;
using Xunit;
using MEAChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MEAChatRole = Microsoft.Extensions.AI.ChatRole;

namespace OpenAgent.Core.Tests.Runtime;

/// <summary>
/// Captures the exact wire JSON the real OpenAI SDK serializes for our message
/// shapes, so provider-side 400s can be diagnosed without touching product code.
/// </summary>
public class OpenAIWireSerializationTests
{
    [Fact]
    public async Task FollowupRequest_KimiStyleColonCallId_SerializesPairedToolExchange()
    {
        // Reproduces the failing production shape: stored turn-1 history (underscore
        // ids, rebuilt through FromStored), a fresh user message, then the FICC-appended
        // assistant tool call + tool result with Kimi's colon-style id.
        CaptureHandler capture = new();
        using IChatClient client = CreateClient(capture);
        List<MEAChatMessage> messages =
        [
            .. CreateStoredTurnHistory(),
            AgentMessageAdapter.CreateUser("看看这个文件", []),
            new MEAChatMessage(MEAChatRole.Assistant,
            [
                new TextReasoningContent("thinking about it"),
                new FunctionCallContent("read_file:0", "read_file",
                    new Dictionary<string, object?> { ["fileId"] = "f-1" })
            ]),
            new MEAChatMessage(MEAChatRole.Tool,
                [new FunctionResultContent("read_file:0", "error: not a text file")])
        ];

        await client.GetResponseAsync(messages);

        Assert.NotNull(capture.RequestBody);
        using JsonDocument document = JsonDocument.Parse(capture.RequestBody);
        List<JsonElement> sent = document.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .ToList();

        // The last serialized assistant tool_call must be the fresh colon-id call.
        int callIndex = -1;
        string? callId = null;
        for (int index = 0; index < sent.Count; index++)
        {
            if (!sent[index].TryGetProperty("tool_calls", out JsonElement toolCalls))
            {
                continue;
            }
            foreach (JsonElement toolCall in toolCalls.EnumerateArray())
            {
                callIndex = index;
                callId = toolCall.GetProperty("id").GetString();
            }
        }

        Assert.Equal("read_file:0", callId);
        bool paired = sent.Skip(callIndex + 1)
            .Any(message => message.TryGetProperty("tool_call_id", out JsonElement toolCallId)
                && toolCallId.GetString() == callId);
        Assert.True(paired, "The serialized request must answer read_file:0 with a tool message.");
    }

    private static IEnumerable<MEAChatMessage> CreateStoredTurnHistory()
    {
        List<ConversationMessage> rows = [CreateRow("user", "hi", null, null, null)];
        for (int index = 0; index < 4; index++)
        {
            string callId = $"read_file_{index}";
            rows.Add(CreateRow(
                "assistant", string.Empty, callId, "read_file",
                new Dictionary<string, string>
                {
                    ["ToolArguments"] = JsonSerializer.Serialize(
                        new Dictionary<string, object?> { ["fileId"] = "f-1" })
                }));
            rows.Add(CreateRow("tool", "file body", callId, null, null));
        }
        rows.Add(CreateRow("assistant", "done describing the file", null, null, null));

        return rows.Select(row => AgentMessageAdapter.FromStored(row))
            .Where(message => message != null)
            .Select(message => message!);
    }

    private static ConversationMessage CreateRow(
        string role,
        string content,
        string? toolCallId,
        string? toolName,
        Dictionary<string, string>? metadata) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        Sequence = 0,
        Role = role,
        Content = content,
        ToolCallId = toolCallId,
        ToolName = toolName,
        Metadata = metadata
    };

    private static IChatClient CreateClient(CaptureHandler capture)
    {
        // Mirrors AgentChatClientFactory.CreateOpenAIChatCompletions, swapping only
        // the transport so the outgoing payload can be captured.
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(capture))
        };
        return new OpenAIClient(new ApiKeyCredential("test-key"), options)
            .GetChatClient("kimi-k2.6")
            .AsIChatClient()
            .AsBuilder()
            .Use(static (messages, options, next, cancellationToken) =>
                next(
                    AgentMessageAdapter.RemoveEmptyOpenAIToolCallText(messages),
                    options,
                    cancellationToken))
            .Build();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"id":"x","object":"chat.completion","created":0,"model":"kimi-k2.6",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},
                                 "finish_reason":"stop"}],
                     "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
