using Microsoft.Extensions.AI;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class NonEmptyAssistantContentChatClientTests
{
    [Fact]
    public async Task ToolCallMessage_IsClonedAndGivenVisibleText()
    {
        var inner = new RecordingChatClient();
        using var client = new NonEmptyAssistantContentChatClient(inner);
        ChatMessage original = new(ChatRole.Assistant,
        [
            new TextContent(string.Empty),
            new TextReasoningContent("inspect first"),
            new FunctionCallContent("call-1", "load_skill", null)
        ]);

        await client.GetResponseAsync([original]);

        ChatMessage forwarded = Assert.Single(inner.Messages!);
        Assert.NotSame(original, forwarded);
        Assert.Equal("[tool call]", forwarded.Text);
        Assert.DoesNotContain(
            forwarded.Contents.OfType<TextContent>(),
            content => string.IsNullOrWhiteSpace(content.Text));
        Assert.Contains(forwarded.Contents, content => content is TextReasoningContent);
        Assert.Single(original.Contents.OfType<TextContent>());
    }

    private sealed class RecordingChatClient : IChatClient
    {
        internal IReadOnlyList<ChatMessage>? Messages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
