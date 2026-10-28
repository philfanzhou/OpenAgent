using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Tests.TestDoubles;

/// <summary>
/// Replays one scripted list of streaming updates per invocation and records every
/// request message list it receives, so tests can assert on followup request contents.
/// When <paramref name="failOnLastTurn"/> is set, the final scripted turn throws it
/// after its updates are replayed, simulating a provider failure mid-run.
/// </summary>
internal sealed class SequenceChatProvider(
    IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> turns,
    Exception? failOnLastTurn = null) : IChatClient
{
    internal List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(messages.ToList());
        int index = Requests.Count - 1;
        IReadOnlyList<ChatResponseUpdate> turn = turns[Math.Min(index, turns.Count - 1)];
        foreach (ChatResponseUpdate update in turn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
        if (failOnLastTurn != null && index >= turns.Count - 1)
        {
            throw failOnLastTurn;
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
