using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>双向 RPC 连接。读取循环只做路由，耗时处理器独立执行，避免反向调用死锁。</summary>
public sealed class AcpRpcConnection(IAcpTransport transport, AcpConnectionOptions? options = null) : IAsyncDisposable
{
    public static ActivitySource ActivitySource { get; } = new("OpenAgent.Runtime.Acp");
    private static readonly Meter Meter = new("OpenAgent.Runtime.Acp");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("acp.rpc.requests");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("acp.rpc.duration", "ms");
    public AcpConnectionOptions Options { get; } = options ?? new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = [];
    private readonly Dictionary<string, Action<JsonElement>> _notifications = [];
    // 双向 ID 可以相同，因此出站等待表与入站取消表必须分离。
    private readonly ConcurrentDictionary<RpcId, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly ConcurrentDictionary<RpcId, Incoming> _incoming = [];
    private readonly ConcurrentDictionary<long, Task> _workers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private long _workerId;
    private int _started;
    private int _disposed;
    private Exception? _failure;
    private int _pendingCount;
    private int _incomingCount;
    private long _incomingBytes;
    public Exception? Failure => _failure;
    /// <summary>连接及有界清理结束；单次 prompt 的完成应等待 AcpPromptTurn.Completion。</summary>
    public Task Completion => _closed.Task;
    public event Action<Exception?>? Closed;
    public int PendingCount => _pending.Count;

    /// <summary>在 Start 之前注册类型化请求处理器；扩展类型可传入自己的序列化元数据。</summary>
    public void On<TRequest, TResponse>(string method, Func<TRequest, CancellationToken, Task<TResponse>> handler,
        JsonTypeInfo<TRequest>? requestType = null, JsonTypeInfo<TResponse>? responseType = null)
    {
        EnsureRegistration();
        requestType ??= AcpJson.TypeInfo<TRequest>();
        responseType ??= AcpJson.TypeInfo<TResponse>();
        _handlers.Add(method, async (json, ct) => JsonSerializer.SerializeToElement(
            await handler(json.Deserialize(requestType) ?? throw new JsonException("Missing parameters."), ct).ConfigureAwait(false), responseType));
    }

    /// <summary>通知回调只能快速校验、入队；等待 UI 或外部 I/O 会堵住响应与取消的路由。</summary>
    public void OnNotification<T>(string method, Action<T> handler)
    {
        EnsureRegistration();
        _notifications.Add(method, json => handler(AcpJson.Read<T>(json)));
    }

    public void Start()
    {
        if (Options.MaxPendingRequests < 1
            || Options.MaxIncomingRequests < 1
            || Options.MaxQueuedUpdates < 1
            || Options.MaxBufferedBytes < 1
            || Options.MaxSessions < 1
            || Options.CancelGracePeriod <= TimeSpan.Zero
            || Options.ShutdownTimeout <= TimeSpan.Zero
            || Options.RequestTimeout != Timeout.InfiniteTimeSpan && Options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Already started.");
        }

        _ = ReadLoopAsync();
    }

    public async Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null,
        JsonTypeInfo<TRequest>? requestType = null, JsonTypeInfo<TResponse>? responseType = null)
    {
        requestType ??= AcpJson.TypeInfo<TRequest>();
        responseType ??= AcpJson.TypeInfo<TResponse>();
        JsonElement result = await RequestRawAsync(method, JsonSerializer.SerializeToElement(request, requestType), cancellationToken, timeout).ConfigureAwait(false);
        return result.Deserialize(responseType) ?? throw new JsonException("Missing result.");
    }

    public async Task<JsonElement> RequestRawAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ThrowIfClosed();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _pendingCount) > Options.MaxPendingRequests)
        {
            Interlocked.Decrement(ref _pendingCount);
            throw new InvalidOperationException("MaxPendingRequests exceeded.");
        }
        RpcId id = new("rpc-" + Interlocked.Increment(ref _nextId));
        TaskCompletionSource<JsonElement> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        using Activity? activity = ActivitySource.StartActivity(method);
        long started = Stopwatch.GetTimestamp();
        Requests.Add(1, new KeyValuePair<string, object?>("rpc.method", method));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        TimeSpan limit = timeout ?? Options.RequestTimeout;
        if (limit != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(limit);
        }

        try
        {
            await SendAsync(new(Id: id.Element, Method: method, Params: parameters), linked.Token).ConfigureAwait(false);
            try
            {
                return await pending.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
                // 即使对端停止读取，取消也必须在限定时间内返回。
                using CancellationTokenSource grace = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                grace.CancelAfter(Options.CancelGracePeriod);
                try
                {
                    await NotifyAsync("$/cancel_request", new CancelRequest(id), grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    SafeCancel(_lifetime);
                }
                throw;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw new IOException("ACP connection closed.", _failure);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            Interlocked.Decrement(ref _pendingCount);
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public Task NotifyAsync<T>(string method, T parameters, CancellationToken cancellationToken = default)
        => SendAsync(new(Method: method, Params: AcpJson.Element(parameters)), cancellationToken);

    private async Task SendAsync(WireMessage message, CancellationToken ct)
    {
        ThrowIfClosed();
        try
        {
            await WriteAsync(JsonSerializer.SerializeToUtf8Bytes(message, AcpJson.TypeInfo<WireMessage>()), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _failure ??= exception;
            SafeCancel(_lifetime);
            throw;
        }
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        Task writing = transport.WriteAsync(bytes, ct);
        try
        {
            await writing.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !writing.IsCompleted)
        {
            // Transport 保证帧原子性；写入卡住时只能关闭连接，不能留下后台半帧继续发送。
            _failure ??= new IOException("ACP write deadline exceeded.");
            SafeCancel(_lifetime);
            _ = writing.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            try
            {
                await transport.DisposeAsync().AsTask().WaitAsync(Options.ShutdownTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await transport.ReadAsync(_lifetime.Token).ConfigureAwait(false) is { } bytes)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(bytes);
                }
                catch (JsonException)
                {
                    await ErrorAsync(null, -32700, "Parse error.").ConfigureAwait(false);
                    continue;
                }
                using (document)
                {
                    JsonElement value = document.RootElement;
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        if (value.GetArrayLength() == 0)
                        {
                            await ErrorAsync(null, -32600, "Empty batch.").ConfigureAwait(false);
                        }
                        else if (value.GetArrayLength() > Options.MaxIncomingRequests)
                        {
                            await ErrorAsync(null, 10002, "Batch limit.").ConfigureAwait(false);
                        }
                        else
                        {
                            // 同步登记每项请求，再交给 worker 等待；批次后紧随的取消不会抢跑。
                            Task<WireMessage>[] responses = value.EnumerateArray().Select(Dispatch).OfType<Task<WireMessage>>().ToArray();
                            if (responses.Length > 0)
                            {
                                Track(() => SendBatchAsync(responses));
                            }
                        }
                    }
                    else if (Dispatch(value) is { } response)
                    {
                        Track(async () => await SendAsync(await response.ConfigureAwait(false), _lifetime.Token).ConfigureAwait(false));
                    }
                }
            }
            throw new EndOfStreamException("ACP peer disconnected.");
        }
        catch (Exception exception)
        {
            _failure ??= exception;
        }
        finally
        {
            Exception failure = _failure ?? new IOException("ACP connection closed.");
            foreach (TaskCompletionSource<JsonElement> item in _pending.Values)
            {
                item.TrySetException(failure);
            }

            SafeCancel(_lifetime);
            foreach (Incoming item in _incoming.Values)
            {
                SafeCancel(item.Source);
            }

            try
            {
                Closed?.Invoke(failure);
            }
            catch (Exception)
            {
                /* 观察者不能阻止连接清理。 */
            }
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            try
            {
                await Task.WhenAll(_workers.Values).WaitAsync(Options.ShutdownTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            _closed.TrySetResult();
        }
    }

    private Task<WireMessage>? Dispatch(JsonElement value)
    {
        static Task<WireMessage> Invalid(string message) => Task.FromResult(new WireMessage(Id: RpcId.Null.Element, Error: new(-32600, message)));
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("jsonrpc", out JsonElement version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
        {
            return Invalid("Invalid request.");
        }

        bool hasId = value.TryGetProperty("id", out JsonElement rawId);
        RpcId? id;
        try
        {
            id = hasId ? AcpJson.Read<RpcId>(rawId) : null;
        }
        catch (JsonException)
        {
            return Invalid("Invalid id.");
        }
        if (!value.TryGetProperty("method", out JsonElement methodValue))
        {
            if (id is null || !_pending.TryRemove(id.Value, out TaskCompletionSource<JsonElement>? pending))
            {
                return null;
            }

            try
            {
                bool hasError = value.TryGetProperty("error", out JsonElement error), hasResult = value.TryGetProperty("result", out JsonElement result);
                if (hasError == hasResult)
                {
                    throw new JsonException("Invalid response.");
                }

                if (hasError)
                {
                    RpcError e = AcpJson.Read<RpcError>(error);
                    pending.TrySetException(new AcpRpcException(e.Code, e.Message, e.Data));
                }
                else
                {
                    pending.TrySetResult(result.Clone());
                }
            }
            catch (JsonException error)
            {
                pending.TrySetException(error);
            }
            return null;
        }
        if (methodValue.ValueKind != JsonValueKind.String)
        {
            return Invalid("Invalid method.");
        }

        string method = methodValue.GetString()!;
        JsonElement parameters = value.TryGetProperty("params", out JsonElement p) ? p.Clone() : AcpJson.Element(new Empty());
        if (id is not null)
        {
            return HandleAsync(id, method, parameters);
        }
        // 通知没有响应；参数错误也不能伪装成 parse error 发回对端。
        try
        {
            if (method == "$/cancel_request")
            {
                if (_incoming.TryGetValue(AcpJson.Read<CancelRequest>(parameters).RequestId, out Incoming? cancel))
                {
                    SafeCancel(cancel.Source);
                }
            }
            else if (_notifications.TryGetValue(method, out Action<JsonElement>? notification))
            {
                notification(parameters);
            }
            else if (_handlers.ContainsKey(method))
            {
                Task<WireMessage> handled = HandleAsync(null, method, parameters);
                Track(async () => { await handled.ConfigureAwait(false); });
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
        }
        return null;
    }

    private async Task SendBatchAsync(Task<WireMessage>[] responses)
    {
        WireMessage[] values = await Task.WhenAll(responses).ConfigureAwait(false);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (WireMessage response in values)
            {
                JsonSerializer.Serialize(writer, response, AcpJson.TypeInfo<WireMessage>());
            }

            writer.WriteEndArray();
        }
        await WriteAsync(stream.ToArray(), _lifetime.Token).ConfigureAwait(false);
    }

    private async Task<WireMessage> HandleAsync(RpcId? id, string method, JsonElement parameters)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        int bytes = System.Text.Encoding.UTF8.GetByteCount(parameters.GetRawText());
        int count = Interlocked.Increment(ref _incomingCount);
        long totalBytes = Interlocked.Add(ref _incomingBytes, bytes);
        if (count > Options.MaxIncomingRequests || totalBytes > Options.MaxBufferedBytes)
        {
            Interlocked.Decrement(ref _incomingCount);
            Interlocked.Add(ref _incomingBytes, -bytes);
            source.Dispose();
            return new(Id: id?.Element, Error: new(10002, "Incoming request limit."));
        }
        if (id is { } key && !_incoming.TryAdd(key, new(source, method, parameters)))
        {
            Interlocked.Decrement(ref _incomingCount);
            Interlocked.Add(ref _incomingBytes, -bytes);
            source.Dispose();
            return new(Id: key.Element, Error: new(-32600, "Duplicate request id."));
        }
        try
        {
            // 先登记取消句柄再切线程；紧随 request 的 cancel 不会丢失，也不会阻塞 reader。
            await Task.Yield();
            if (!_handlers.TryGetValue(method, out var handler))
            {
                return new(Id: id?.Element, Error: new(-32601, "Method not found."));
            }

            JsonElement result = await handler(parameters, source.Token).ConfigureAwait(false);
            return new(Id: id?.Element, Result: result);
        }
        catch (AcpRpcException error)
        {
            return new(Id: id?.Element, Error: new(error.Code, error.Message, error.ErrorData));
        }
        catch (OperationCanceledException)
        {
            return new(Id: id?.Element, Error: new(-32800, "Request cancelled."));
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return new(Id: id?.Element, Error: new(-32602, "Invalid parameters."));
        }
        catch (Exception)
        {
            return new(Id: id?.Element, Error: new(-32603, "Internal error."));
        }
        finally
        {
            if (id is { } k) { _incoming.TryRemove(k, out _);
            } Interlocked.Decrement(ref _incomingCount);
            Interlocked.Add(ref _incomingBytes, -bytes);
            source.Dispose();
        }
    }

    private Task ErrorAsync(RpcId? id, int code, string message) => SendAsync(new(Id: (id ?? RpcId.Null).Element, Error: new(code, message)), _lifetime.Token);
    private void Track(Func<Task> action)
    {
        if (_workers.Count > Options.MaxIncomingRequests + Options.MaxPendingRequests)
        {
            _failure ??= new IOException("Dispatcher queue limit exceeded.");
            SafeCancel(_lifetime);
            return;
        }
        long id = Interlocked.Increment(ref _workerId);
        Task task = Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _failure ??= error;
                SafeCancel(_lifetime);
            }
        });
        _workers[id] = task;
        _ = task.ContinueWith(t => { _ = t.Exception; _workers.TryRemove(id, out _); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal void CancelRequests(Func<string, JsonElement, bool> predicate)
    {
        foreach (Incoming item in _incoming.Values)
        {
            if (predicate(item.Method, item.Parameters))
            {
                SafeCancel(item.Source);
            }
        }
    }
    private sealed record Incoming(CancellationTokenSource Source, string Method, JsonElement Parameters);
    private void EnsureRegistration()
    {
        if (_started != 0)
        {
            throw new InvalidOperationException("Register handlers before Start.");
        }
    }
    private void SafeCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            _failure ??= error;
        }
    }
    private void ThrowIfClosed()
    {
        if (_started == 0)
        {
            throw new InvalidOperationException("Start the connection first.");
        }

        if (_lifetime.IsCancellationRequested || _disposed != 0)
        {
            throw new IOException("ACP connection closed.", _failure);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            SafeCancel(_lifetime);
            await transport.DisposeAsync().ConfigureAwait(false);
            if (_started == 0)
            {
                _closed.TrySetResult();
            }
        }
        await Completion.ConfigureAwait(false);
    }
}
