using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>
/// One bidirectional JSON-RPC engine over a single transport. The reader never waits on
/// handlers: inbound requests dispatch onto independent tasks, notifications keep wire
/// order through one bounded queue, and outbound calls live in a pending table that is
/// separate from inbound dispatch, so reentrant reverse calls always complete.
/// </summary>
/// <remarks>
/// Registration freezes at <see cref="Start"/>; the persistent reader starts there and runs
/// until EOF, a fault, or disposal. Every spawned task is tracked and observed by
/// <see cref="Completion"/>. The engine validates known-method payloads against the pinned
/// schema and speaks only its own public types; no third-party RPC type crosses this API.
/// </remarks>
public sealed partial class AcpRpcConnection
{
    internal const string CancelRequestMethod = "$/cancel_request";

    private const int WriteIdle = 0;
    private const int WriteCancelled = 1;
    private const int WriteClaimed = 2;

    private readonly IAcpTransport _transport;
    private readonly AcpRpcRole _role;
    private readonly AcpRpcOptions _options;
    private readonly string _inboundRequestEnvelope;
    private readonly string _inboundNotificationEnvelope;
    private readonly string _outboundRequestEnvelope;
    private readonly string _outboundNotificationEnvelope;
    private readonly string _outboundResponseEnvelope;
    private readonly string _inboundResponseEnvelope;
    private readonly Dictionary<string, AcpMethodDescriptor> _catalog;
    private readonly Dictionary<string, RegisteredHandler> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<AcpRpcNotification, CancellationToken, Task>> _notificationHandlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _recentlyCompleted = new();
    private readonly ConcurrentDictionary<string, byte> _recentlyCompletedSet = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _tracked = new();
    private readonly ConcurrentDictionary<BatchCollector, byte> _activeBatches = new();
    private readonly Channel<OutboundWrite> _outgoing;
    private readonly Channel<AcpRpcNotification> _notifications;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateGate = new();
    private readonly object _drainGate = new();
    private readonly List<TaskCompletionSource> _drainWaiters = [];
    private readonly List<Exception> _faults = [];
    private long _notificationsQueued;
    private long _notificationsDispatched;
    private int _nextId;
    private int _activeHandlers;
    private bool _started;
    private bool _closed;
    private Task? _shutdown;

    public AcpRpcConnection(IAcpTransport transport, AcpRpcRole role, AcpRpcOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _role = role;
        _options = options ?? new AcpRpcOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxPendingRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxConcurrentHandlers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxQueuedNotifications);
        if (_options.OutboundIdPrefix is null) throw new ArgumentException("OutboundIdPrefix must not be null.", nameof(options));
        bool client = role == AcpRpcRole.Client;
        (_inboundRequestEnvelope, _inboundNotificationEnvelope) = client ? ("AgentRequest", "AgentNotification") : ("ClientRequest", "ClientNotification");
        (_outboundRequestEnvelope, _outboundNotificationEnvelope) = client ? ("ClientRequest", "ClientNotification") : ("AgentRequest", "AgentNotification");
        _inboundResponseEnvelope = client ? "AgentResponse" : "ClientResponse";
        _outboundResponseEnvelope = client ? "ClientResponse" : "AgentResponse";
        _catalog = AcpProtocol.Methods.ToDictionary(method => method.Name, StringComparer.Ordinal);
        _outgoing = Channel.CreateBounded<OutboundWrite>(new BoundedChannelOptions(_options.MaxPendingRequests + _options.MaxConcurrentHandlers + 256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _notifications = Channel.CreateBounded<AcpRpcNotification>(new BoundedChannelOptions(_options.MaxQueuedNotifications)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Completes after the terminal reason (EOF, fault, or disposal), every inbound handler, and the transport itself settle.</summary>
    /// <remarks>Terminal transport or cleanup faults fault this task; a clean EOF or disposal completes it.</remarks>
    public Task Completion => _completion.Task;

    private bool IsClosed
    {
        get { lock (_stateGate) return _closed; }
    }

    private bool IsStarted
    {
        get { lock (_stateGate) return _started; }
    }

    /// <summary>Starts the persistent reader and freezes handler registration. Call once, after registration.</summary>
    public void Start()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_started) throw new InvalidOperationException("The connection has already started.");
            _started = true;
        }

        Track(Task.Run(RunReaderAsync));
        Track(Task.Run(RunWriterLoopAsync));
        Track(Task.Run(RunNotificationLoopAsync));
    }

    /// <summary>Registers the handler for one request method, standard or extension. Registration is explicit and frozen at start.</summary>
    public void RegisterRequestHandler(string method, Func<AcpRpcRequest, CancellationToken, Task<JsonElement?>> handler)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_stateGate)
        {
            if (_started) throw new InvalidOperationException("Handler registration is frozen once the connection has started.");
            if (!_requestHandlers.TryAdd(method, new RegisteredHandler(handler))) throw new ArgumentException($"A handler for '{method}' is already registered.", nameof(method));
        }
    }

    /// <summary>Registers the handler for one notification method. The engine keeps notification delivery in wire order.</summary>
    public void RegisterNotificationHandler(string method, Func<AcpRpcNotification, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_stateGate)
        {
            if (_started) throw new InvalidOperationException("Handler registration is frozen once the connection has started.");
            if (!_notificationHandlers.TryAdd(method, handler)) throw new ArgumentException($"A handler for '{method}' is already registered.", nameof(method));
        }
    }

    /// <summary>
    /// Sends one request and awaits its response. A cancellation or timeout before the frame
    /// is written cancels locally; afterwards the engine sends $/cancel_request and waits
    /// <see cref="AcpRpcOptions.CancelGracePeriod"/> for the peer's final response, closing the
    /// connection unconfirmed when the peer never answers.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!IsStarted) throw new InvalidOperationException("The connection has not started; call Start first.");
        PendingCall call = RegisterPending(method);
        try
        {
            byte[] frame = BuildValidatedRequestFrame(call.Id, method, parameters);
            OutboundWrite write = new() { Frame = frame, Written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            if (_options.RequestTimeout is { } timeout) linked.CancelAfter(timeout);
            CancellationTokenRegistration registration = linked.Token.Register(() => OnCallerCancel(call, write));
            try
            {
                await _outgoing.Writer.WriteAsync(write, linked.Token).ConfigureAwait(false);
                await write.Written.Task.ConfigureAwait(false);
                return await call.Response.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (call.CancelledLocally)
            {
                throw new OperationCanceledException("The request was cancelled before it was written.", linked.Token);
            }
            finally
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(call.Key, out _);
        }
    }

    /// <summary>Sends one notification and completes once the frame is on the wire.</summary>
    public async Task NotifyAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!IsStarted) throw new InvalidOperationException("The connection has not started; call Start first.");
        if (method == CancelRequestMethod) throw new ArgumentException("Cancel requests through RequestAsync cancellation; $/cancel_request is engine-managed.", nameof(method));
        byte[] frame = BuildValidatedNotificationFrame(method, parameters);
        OutboundWrite write = new() { Frame = frame, Written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _outgoing.Writer.WriteAsync(write, linked.Token).ConfigureAwait(false);
        await write.Written.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Close(reason: "Dispose");
        // Terminal faults stay observable on Completion; disposal reports only what closing itself did.
        await _completion.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Test-only observation: tracked tasks still running, so leak regressions can assert a drained engine.</summary>
    internal int TrackedTaskCount => _tracked.Count;

    /// <summary>
    /// Waits until every notification the reader has queued so far finished dispatching to its
    /// handler. The role layer awaits this between a turn's final response and reporting
    /// completion, so same-session updates that preceded the response on the wire are already in
    /// their session queues (DESIGN: updates enqueue before the turn completes). The wait stays
    /// bounded by the connection lifetime: shutdown cancels the waiters it will never dispatch.
    /// </summary>
    internal async Task AwaitNotificationDrainAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? waiter = RegisterDrainWaiter();
        if (waiter is null) return;
        try
        {
            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_drainGate) _drainWaiters.Remove(waiter);
            throw;
        }
    }

    private TaskCompletionSource? RegisterDrainWaiter()
    {
        lock (_drainGate)
        {
            if (Interlocked.Read(ref _notificationsDispatched) >= Interlocked.Read(ref _notificationsQueued)) return null;
            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainWaiters.Add(waiter);
            return waiter;
        }
    }

    /// <summary>Called by the notification loop after each dispatch; releases drain waiters once the queue caught up.</summary>
    private void SignalNotificationDispatch()
    {
        long dispatched = Interlocked.Increment(ref _notificationsDispatched);
        TaskCompletionSource[]? ready = null;
        lock (_drainGate)
        {
            if (_drainWaiters.Count > 0 && dispatched >= Interlocked.Read(ref _notificationsQueued))
            {
                ready = [.. _drainWaiters];
                _drainWaiters.Clear();
            }
        }

        if (ready is null) return;
        foreach (TaskCompletionSource waiter in ready) waiter.TrySetResult();
    }

    private void CancelDrainWaiters()
    {
        TaskCompletionSource[]? waiters;
        lock (_drainGate)
        {
            waiters = _drainWaiters.Count > 0 ? [.. _drainWaiters] : null;
            _drainWaiters.Clear();
        }

        if (waiters is null) return;
        foreach (TaskCompletionSource waiter in waiters) waiter.TrySetCanceled();
    }

    private static string PendingKey(RpcId id) => id.Kind == JsonValueKind.String
        ? "s:" + id.StringValue
        : "n:" + id.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private PendingCall RegisterPending(string method)
    {
        lock (_stateGate)
        {
            if (_closed) throw new AcpConnectionClosedException("connection closed");
            if (_pending.Count >= _options.MaxPendingRequests) throw new AcpQuotaExceededException(nameof(AcpRpcOptions.MaxPendingRequests), _options.MaxPendingRequests, _pending.Count + 1);
            PendingCall call = new(method, $"{_options.OutboundIdPrefix}{Interlocked.Increment(ref _nextId)}");
            _pending[call.Key] = call;
            return call;
        }
    }

    private void OnCallerCancel(PendingCall call, OutboundWrite write)
    {
        int previous = Interlocked.CompareExchange(ref write.State, WriteCancelled, WriteIdle);
        if (previous != WriteIdle)
        {
            // The writer already claimed the frame, so it is on or entering the wire: cancel through the protocol.
            StartCancelFlow(call, sendNotification: true);
            return;
        }

        lock (call.Gate)
        {
            if (call.State != PendingState.Awaiting) return;
            call.State = PendingState.CancelledBeforeWrite;
            _pending.TryRemove(call.Key, out _);
            call.Response.TrySetCanceled();
        }
    }

    /// <summary>Begins the bounded wait for the peer's final answer after a cancel applies. Idempotent.</summary>
    private void StartCancelFlow(PendingCall call, bool sendNotification)
    {
        CancellationTokenSource? grace = null;
        lock (call.Gate)
        {
            if (call.State != PendingState.Awaiting) return;
            call.State = PendingState.CancelSent;
            grace = new CancellationTokenSource();
            call.Grace = grace;
        }

        if (sendNotification) _ = Track(SendCancelNotificationAsync(call));
        _ = Track(TrackGracePeriodAsync(call, grace));
    }

    private async Task SendCancelNotificationAsync(PendingCall call)
    {
        try
        {
            byte[] frame = BuildCancelFrame(call.Id);
            OutboundWrite write = new() { Frame = frame, Written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            await _outgoing.Writer.WriteAsync(write, CancellationToken.None).ConfigureAwait(false);
            await write.Written.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            FailPending(call, new AcpConnectionClosedException("the cancel notification could not be written", exception));
        }
    }

    private async Task TrackGracePeriodAsync(PendingCall call, CancellationTokenSource grace)
    {
        try
        {
            await Task.Delay(_options.CancelGracePeriod, grace.Token).ConfigureAwait(false);
            bool unconfirmed;
            lock (call.Gate) unconfirmed = call.State == PendingState.CancelSent;
            if (!unconfirmed) return;
            lock (call.Gate) call.State = PendingState.Unconfirmed;
            Close($"cancel grace elapsed for '{call.Method}'");
            FailPending(call, new AcpCancellationUnconfirmedException(call.Method, call.Id));
        }
        catch (OperationCanceledException)
        {
            // The response arrived or the connection closed first; the pending result decides.
        }
        finally
        {
            grace.Dispose();
        }
    }

    private void FailPending(PendingCall call, Exception exception)
    {
        _pending.TryRemove(call.Key, out _);
        call.Response.TrySetException(exception);
    }

    /// <summary>Completes a pending call exactly once. Late or duplicate arrivals already fell through to diagnostics.</summary>
    private void CompletePending(PendingCall call, JsonElement? result, Error? error)
    {
        lock (call.Gate)
        {
            if (call.State is PendingState.CancelledBeforeWrite or PendingState.Unconfirmed or PendingState.Completed) return;
            call.State = PendingState.Completed;
            call.Grace?.Cancel();
        }

        _pending.TryRemove(call.Key, out _);
        if (_recentlyCompletedSet.Count < 128) _recentlyCompletedSet[call.Key] = 0;
        _recentlyCompleted.Enqueue(call.Key);
        while (_recentlyCompleted.Count > 128 && _recentlyCompleted.TryDequeue(out string? evicted)) _recentlyCompletedSet.TryRemove(evicted, out _);
        if (error is not null) call.Response.TrySetException(new AcpRpcException(error));
        else call.Response.TrySetResult(result!.Value);
    }

    private Task Track(Task task)
    {
        _tracked[task] = 0;
        _ = task.ContinueWith(static (finished, state) =>
        {
            ((ConcurrentDictionary<Task, byte>)state!).TryRemove(finished, out _);
            // Every tracked task handles its own exceptions; nothing is left unobserved.
            _ = finished.Exception;
        }, _tracked, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private void Diagnostic(AcpRpcDiagnosticKind kind, string? method, RpcId? id, string detail)
    {
        try { _options.Diagnostic?.Invoke(new AcpRpcDiagnosticEvent(kind, method, id, detail)); }
        catch
        {
            // A failing sink never breaks the protocol path.
        }
    }

    private sealed record RegisteredHandler(Func<AcpRpcRequest, CancellationToken, Task<JsonElement?>> Handler);

    private enum PendingState
    {
        Awaiting,
        CancelledBeforeWrite,
        CancelSent,
        Unconfirmed,
        Completed
    }

    private sealed class PendingCall
    {
        public PendingCall(string method, string id)
        {
            Method = method;
            Id = id;
            Key = "s:" + id;
            Response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string Method { get; }
        public string Id { get; }
        /// <summary>Pending-table key; engine-generated IDs are always strings.</summary>
        public string Key { get; }
        public TaskCompletionSource<JsonElement> Response { get; }
        public object Gate { get; } = new();
        public PendingState State { get; set; }
        public CancellationTokenSource? Grace { get; set; }
        public bool CancelledLocally
        {
            get { lock (Gate) return State == PendingState.CancelledBeforeWrite; }
        }
    }

    private sealed class OutboundWrite
    {
        public required byte[] Frame { get; init; }
        public required TaskCompletionSource Written { get; init; }
        public int State;
    }
}
