using OpenAgent.Contracts.Security;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Core.Execution;

internal sealed class AcpConnection : IAsyncDisposable
{
    private const int MaxMessageCharacters = 1024 * 1024;
    private readonly Process _process;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonElement> _updates = Channel.CreateBounded<JsonElement>(256);
    private readonly string _permissionPolicy;
    private readonly Task _reader;
    private readonly Task _stderr;
    private long _nextId;
    private bool _cancelling;
    internal bool CaptureUpdates { get; set; }
    internal string? SessionId { get; set; }
    internal ChannelReader<JsonElement> Updates => _updates.Reader;

    internal AcpConnection(ProcessStartInfo start, string permissionPolicy)
    {
        _permissionPolicy = permissionPolicy;
        _process = Process.Start(start) ?? throw new InvalidOperationException("The ACP harness could not be started.");
        _stderr = DrainErrorAsync();
        _reader = ReadAsync();
    }

    internal async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    internal Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(message);
        if (json.Length > MaxMessageCharacters)
            throw new InvalidOperationException("The ACP message exceeds the size limit.");
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception failure = new InvalidOperationException("The ACP harness disconnected before completing the request.");
        try
        {
            char[] buffer = new char[4096];
            var line = new StringBuilder();
            while (true)
            {
                int count = await _process.StandardOutput.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                    break;
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (line.Length > 0)
                        {
                            using JsonDocument document = JsonDocument.Parse(line.ToString());
                            await DispatchAsync(document.RootElement).ConfigureAwait(false);
                            line.Clear();
                        }
                    }
                    else
                    {
                        line.Append(buffer[i]);
                        if (line.Length > MaxMessageCharacters)
                            throw new InvalidOperationException("The ACP message exceeds the size limit.");
                    }
                }
            }
        }
        catch (Exception)
        { failure = new InvalidOperationException("The ACP harness disconnected or returned invalid protocol output."); }
        finally
        {
            foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
                pending.TrySetException(failure);
            _updates.Writer.TryComplete(failure);
        }
    }

    private async Task DispatchAsync(JsonElement message)
    {
        if (message.TryGetProperty("method", out JsonElement method))
        {
            string? name = method.GetString();
            if (message.TryGetProperty("id", out JsonElement id))
            {
                if (name == "session/request_permission")
                {
                    JsonElement parameters = message.GetProperty("params");
                    string kind = _permissionPolicy == "allow_once" ? "allow_once" : "reject_once";
                    string? option = parameters.GetProperty("options").EnumerateArray()
                        .Where(item => item.GetProperty("kind").GetString() == kind)
                        .Select(item => item.GetProperty("optionId").GetString()).FirstOrDefault();
                    object outcome = option == null || _cancelling || _lifetime.IsCancellationRequested
                        || parameters.GetProperty("sessionId").GetString() != SessionId
                        ? new { outcome = "cancelled" }
                        : (object)new { outcome = "selected", optionId = option };
                    await SendAsync(new { jsonrpc = "2.0", id = id.Clone(), result = new { outcome } }, _lifetime.Token).ConfigureAwait(false);
                }
                else
                {
                    await SendAsync(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Client capability is not supported." } }, _lifetime.Token).ConfigureAwait(false);
                }
            }
            else if (name == "session/update" && CaptureUpdates)
            {
                JsonElement parameters = message.GetProperty("params");
                if (parameters.GetProperty("sessionId").GetString() == SessionId)
                    await _updates.Writer.WriteAsync(parameters.GetProperty("update").Clone(), _lifetime.Token).ConfigureAwait(false);
            }
            return;
        }
        if (message.TryGetProperty("id", out JsonElement responseId) && responseId.TryGetInt64(out long value)
            && _pending.TryGetValue(value, out TaskCompletionSource<JsonElement>? completion))
        {
            if (message.TryGetProperty("error", out JsonElement error))
                completion.TrySetException(new AgentException(AgentErrorCode.InvalidRequest,
                    $"The ACP harness rejected the request (RPC code {error.GetProperty("code").GetInt32()})."));
            else
                completion.TrySetResult(message.GetProperty("result").Clone());
        }
    }

    private async Task DrainErrorAsync()
    {
        // Drain without retaining or logging stderr: harnesses may print credentials or prompts.
        char[] buffer = new char[4096];
        try
        {
            while (await _process.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false) > 0)
            { }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
    }

    internal async Task CancelAsync(string sessionId)
    {
        _cancelling = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        { await NotifyAsync("session/cancel", new { sessionId }, timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        { _process.StandardInput.Close(); }
        catch (IOException) { }
        if (!_process.HasExited)
        {
            try
            { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        await Task.WhenAll(_reader, _stderr).ConfigureAwait(false);
        _process.Dispose();
        _writer.Dispose();
        _lifetime.Dispose();
    }
}
