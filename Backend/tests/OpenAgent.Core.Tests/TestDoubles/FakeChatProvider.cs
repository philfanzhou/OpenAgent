using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Tests.TestDoubles;

internal sealed class FakeChatProvider : IChatClient
{
    private readonly ChatResponse? _response;
    private readonly IReadOnlyList<ChatResponseUpdate> _updates;
    private readonly Exception? _exception;

    internal IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];
    internal ChatOptions? LastOptions { get; private set; }
    internal int CallCount { get; private set; }

    internal FakeChatProvider(ChatResponse response)
    {
        _response = response;
        _updates = [];
    }

    internal FakeChatProvider(IReadOnlyList<ChatResponseUpdate> updates)
    {
        _updates = updates;
    }

    internal FakeChatProvider(Exception exception)
    {
        _exception = exception;
        _updates = [];
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastMessages = messages.ToList();
        LastOptions = options;
        CallCount++;
        return _exception != null
            ? Task.FromException<ChatResponse>(_exception)
            : Task.FromResult(_response ?? throw new InvalidOperationException("No fake response was configured."));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastMessages = messages.ToList();
        LastOptions = options;
        CallCount++;
        if (_exception != null)
        {
            throw _exception;
        }

        foreach (ChatResponseUpdate update in _updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
