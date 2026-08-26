using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Tests.TestDoubles;

/// <summary>
/// Replays one scripted list of streaming updates per invocation and records every
/// request message list it receives, so tests can assert on followup request contents.
/// </summary>
internal sealed class SequenceChatProvider(IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> turns) : IChatClient
{
    internal List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(messages.ToList());
        IReadOnlyList<ChatResponseUpdate> turn = turns[Math.Min(Requests.Count - 1, turns.Count - 1)];
        foreach (ChatResponseUpdate update in turn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SequenceChatProvider only supports streaming responses.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
