using System.Text.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace OpenAgent.Runtime.Acp.Tests.Rpc.EngineComparison;

/// <summary>An in-memory duplex stream pair: writes to one side arrive on the other side's reads.</summary>
internal sealed class DuplexStream : Stream
{
    private readonly object _gate = new();
    private readonly MemoryStream _readBuffer = new();
    private readonly SemaphoreSlim _available = new(0);
    private DuplexStream _peer = null!;
    private bool _closed;

    private DuplexStream() { }

    public static (DuplexStream Local, DuplexStream Remote) CreatePair()
    {
        DuplexStream local = new();
        DuplexStream remote = new();
        local._peer = remote;
        remote._peer = local;
        return (local, remote);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _readBuffer.Position = 0;
                int read = _readBuffer.Read(buffer.Span);
                if (read > 0)
                {
                    byte[] rest = new byte[(int)(_readBuffer.Length - read)];
                    _ = _readBuffer.Read(rest);
                    _readBuffer.SetLength(0);
                    _readBuffer.Write(rest);
                    return read;
                }
            }
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_closed || _peer._closed) throw new IOException("The duplex stream is closed.");
        lock (_peer._gate)
        {
            _peer._readBuffer.Seek(0, SeekOrigin.End);
            _peer._readBuffer.Write(buffer.Span);
        }

        _peer._available.Release();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override bool CanRead => !_closed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_closed;
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use async overloads.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use async overloads.");
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        _available.Release();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Drives StreamJsonRpc 2.25.29 through its public API over newline-delimited framing:
/// managed invocations, notifications, and local handlers. Wire shapes its public API cannot
/// produce (forced ID values, batch arrays, raw inbound frames) are recorded as not
/// exercisable in the probe matrix rather than hand-forged.
/// </summary>
internal sealed class StreamJsonRpcAdapter : IAsyncDisposable
{
    private readonly Dictionary<string, Delegate> _handlers = new(StringComparer.Ordinal);
    private readonly List<(string Method, string Json)> _notifications = new();
    private readonly object _notificationGate = new();
    private JsonRpc? _rpc;

    public static (StreamJsonRpcAdapter Local, StreamJsonRpcAdapter Remote) CreatePair()
    {
        StreamJsonRpcAdapter local = new();
        StreamJsonRpcAdapter remote = new();
        return (local, remote);
    }

    /// <summary>Connects the two adapters and starts listening on both sides.</summary>
    public void Attach(StreamJsonRpcAdapter peer)
    {
        (DuplexStream localWire, DuplexStream remoteWire) = DuplexStream.CreatePair();
        _rpc = new JsonRpc(new NewLineDelimitedMessageHandler(localWire, localWire, new JsonMessageFormatter()));
        peer._rpc = new JsonRpc(new NewLineDelimitedMessageHandler(remoteWire, remoteWire, new JsonMessageFormatter()));
        foreach ((string method, Delegate handler) in _handlers) _rpc.AddLocalRpcMethod(method, handler);
        foreach ((string method, Delegate handler) in peer._handlers) peer._rpc.AddLocalRpcMethod(method, handler);
        _rpc.StartListening();
        peer._rpc.StartListening();
    }

    public void OnRequest(string method, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        _handlers[method] = Wrap(handler);
    }

    private static Func<JToken, CancellationToken, Task<JToken>> Wrap(Func<JsonElement, CancellationToken, Task<JsonElement>> handler) =>
        async (token, cancellationToken) =>
        {
            JsonElement input = token.Type is JTokenType.Null
                ? JsonDocument.Parse("null").RootElement.Clone()
                : JsonDocument.Parse(token.ToString(Newtonsoft.Json.Formatting.None)).RootElement.Clone();
            JsonElement output = await handler(input, cancellationToken).ConfigureAwait(false);
            return JToken.Parse(JsonSerializer.Serialize(output));
        };

    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        JToken argument = parameters.HasValue ? JToken.Parse(JsonSerializer.Serialize(parameters.Value)) : JValue.CreateNull();
        JToken result = await _rpc!.InvokeWithParameterObjectAsync<JToken>(method, argument, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(result.ToString(Newtonsoft.Json.Formatting.None)).RootElement.Clone();
    }

    public void Notify(string method, object parameters) => _rpc!.NotifyAsync(method, parameters);

    /// <summary>Notifications this side received, for convention comparison.</summary>
    public (string Method, string Json)[] Notifications
    {
        get { lock (_notificationGate) return [.. _notifications]; }
    }

    public async Task ShutdownAsync() => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_rpc is not null)
        {
            _rpc.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
