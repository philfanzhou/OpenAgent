using System.Buffers;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Protocol.Serialization;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Rpc;

public sealed partial class AcpRpcConnection
{
    private async Task RunWriterLoopAsync()
    {
        try
        {
            await foreach (OutboundWrite write in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Interlocked.CompareExchange(ref write.State, WriteClaimed, WriteIdle) != WriteIdle)
                {
                    // Cancelled before the writer claimed it: skip without touching the wire.
                    write.Written.TrySetCanceled();
                    continue;
                }

                try
                {
                    await _transport.WriteAsync(write.Frame, _lifetime.Token).ConfigureAwait(false);
                    write.Written.TrySetResult();
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    write.Written.TrySetCanceled();
                }
                catch (Exception exception)
                {
                    // The fault closes the connection, so the call fails closed like every other
                    // pending call (PROTOCOL §4); the raw transport error stays on Completion.
                    write.Written.TrySetException(new AcpConnectionClosedException("a frame could not be written", exception));
                    Close("a frame could not be written", exception);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: queued frames fail through the drain below.
        }
        catch (Exception exception)
        {
            Close("the writer loop failed", exception);
            return;
        }

        DrainPendingWrites();
    }

    private void DrainPendingWrites()
    {
        while (_outgoing.Reader.TryRead(out OutboundWrite? write))
        {
            write.Written.TrySetException(new AcpConnectionClosedException("connection closing"));
        }
    }

    private async Task EnqueueFrameAsync(byte[] frame)
    {
        OutboundWrite write = new() { Frame = frame, Written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await _outgoing.Writer.WriteAsync(write, CancellationToken.None).ConfigureAwait(false);
        await write.Written.Task.ConfigureAwait(false);
    }

    /// <summary>Initiates one close. Idempotent; a terminal transport fault decides whether Completion faults.</summary>
    private void Close(string reason, Exception? fault = null)
    {
        Task? shutdown;
        lock (_stateGate)
        {
            if (_shutdown is null)
            {
                _closed = true;
                if (fault is not null) _faults.Add(fault);
                // The closer must not observe itself: ShutdownAsync waits on every other tracked task.
                _shutdown = Task.Run(() => ShutdownAsync(reason));
                try
                {
                    _lifetime.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            shutdown = _shutdown;
        }

        _ = shutdown;
    }

    private async Task ShutdownAsync(string reason)
    {
        _notifications.Writer.TryComplete();
        _outgoing.Writer.TryComplete();
        List<Exception> failures = [.. _faults];
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (CancellationTokenSource cancellation in _inflight.Values)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        FailAllPending(new AcpConnectionClosedException(reason));
        // Converge in rounds: unfinished batch collectors are settled first (their handlers may
        // already have stopped writing frames), then every tracked task must drain. A task
        // tracked after one snapshot is caught by the next round, so shutdown leaves no
        // unobserved task behind (PROTOCOL §4). Each round only waits on bounded work.
        Error closing = new(ErrorCode.InternalError, "The connection is closing.");
        while (true)
        {
            foreach (BatchCollector collector in _activeBatches.Keys) collector.Abandon(closing);
            Task[] snapshot = [.. _tracked.Keys];
            if (snapshot.Length == 0) break;
            await Task.WhenAll(snapshot).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        // Disposing first lets a dispose-initiated close settle; the transport's own EOF path already runs.
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await _transport.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        Exception? terminal = failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures)
        };
        if (terminal is null) _completion.TrySetResult();
        else _completion.TrySetException(terminal);
    }

    private void FailAllPending(AcpConnectionClosedException reason)
    {
        foreach ((string _, PendingCall call) in _pending)
        {
            lock (call.Gate)
            {
                // An elapsed cancel owns its outcome: the grace flow reports AcpCancellationUnconfirmedException
                // even when shutdown races it, so a close never masks the cancellation that triggered it.
                if (call.State == PendingState.Unconfirmed) continue;
            }

            if (_pending.TryRemove(call.Key, out _))
            {
                lock (call.Gate)
                {
                    call.Grace?.Cancel();
                }

                call.Response.TrySetException(reason);
            }
        }

        while (_notifications.Reader.TryRead(out _)) { }
        DrainPendingWrites();
    }

    // ---- Frame construction ----

    private static JsonElement BuildElement(Action<Utf8JsonWriter> action)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer)) action(writer);
        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private byte[] BuildValidatedRequestFrame(string id, string method, JsonElement? parameters)
    {
        JsonElement envelope = BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            writer.WriteStringValue(id);
            writer.WriteString("method", method);
            if (parameters is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value)
            {
                writer.WritePropertyName("params");
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        });
        return ToFrame(SchemaValidator.Process(_outboundRequestEnvelope, envelope, compatibility: false));
    }

    private byte[] BuildValidatedNotificationFrame(string method, JsonElement? parameters)
    {
        JsonElement envelope = BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            if (parameters is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value)
            {
                writer.WritePropertyName("params");
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        });
        return ToFrame(SchemaValidator.Process(_outboundNotificationEnvelope, envelope, compatibility: false));
    }

    private byte[] BuildCancelFrame(string requestId)
    {
        JsonElement envelope = BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("method", CancelRequestMethod);
            writer.WriteStartObject("params");
            writer.WritePropertyName("requestId");
            writer.WriteStringValue(requestId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        // The protocol-level cancel notification has no envelope union upstream; validate at definition level.
        SchemaValidator.Process("CancelRequestNotification", envelope.GetProperty("params"), compatibility: false);
        return ToFrame(envelope);
    }

    /// <summary>Builds one response object; exactly one of result/error must be set.</summary>
    private JsonElement BuildResponseObject(RpcId id, JsonElement? result, Error? error)
    {
        if ((result is null) == (error is null)) throw new InvalidOperationException("A response carries exactly one of result and error.");
        return BuildElement(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            JsonSerializer.Serialize(writer, id, AcpJson.TypeInfo<RpcId>());
            if (result is { } value)
            {
                writer.WritePropertyName("result");
                if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                else
                {
                    value.WriteTo(writer);
                }
            }
            else
            {
                writer.WritePropertyName("error");
                JsonSerializer.Serialize(writer, error!, AcpJson.TypeInfo<Error>());
            }

            writer.WriteEndObject();
        });
    }

    private async Task WriteErrorResponseAsync(RpcId? id, int code, string message, Exception? exception = null, JsonElement? data = null)
    {
        try
        {
            Error error = data is null ? new Error(code, message) : new Error(code, message, Optional<JsonElement>.FromValue(data.Value));
            JsonElement response = SchemaValidator.Process(_outboundResponseEnvelope, BuildResponseObject(id ?? RpcId.Null, null, error), compatibility: false);
            await EnqueueFrameAsync(ToFrame(response)).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Close("an error response could not be written", failure);
        }
    }

    private static byte[] ToFrame(JsonElement element)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer)) element.WriteTo(writer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reserves ordered batch response slots on the reader thread; handlers fill them without blocking it.</summary>
    private sealed class BatchCollector
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _slots = [];
        private readonly List<BatchItem?> _items = [];
        private readonly HashSet<int> _open = [];
        private int _expected = -1;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int NextSlot()
        {
            lock (_gate)
            {
                _slots.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                _items.Add(null);
                _open.Add(_slots.Count - 1);
                return _slots.Count - 1;
            }
        }

        public void Complete(int slot, RpcId id, JsonElement validatedResult) => Fill(slot, new BatchItem(id, null, validatedResult));

        public void Fail(int slot, RpcId? id, Error error) => Fill(slot, new BatchItem(id, error, null));

        private void Fill(int slot, BatchItem item)
        {
            lock (_gate)
            {
                // Shutdown's sweep may have settled this slot first; the first fill wins.
                if (!_open.Remove(slot)) return;
                _items[slot] = item;
                if (_expected >= 0 && _open.Count == 0) _done.TrySetResult();
            }
        }

        /// <summary>Shutdown path: settles every slot no handler will fill, so assembly and disposal converge.</summary>
        public void Abandon(Error error)
        {
            lock (_gate)
            {
                foreach (int slot in _open) _items[slot] = new BatchItem(null, error, null);
                bool settled = _open.Count > 0;
                _open.Clear();
                if (settled && _expected >= 0) _done.TrySetResult();
            }
        }

        public void Seal()
        {
            lock (_gate)
            {
                _expected = _slots.Count;
                if (_open.Count == 0) _done.TrySetResult();
            }
        }

        public Task CollectAsync() => _done.Task;

        public BatchItem[] Items
        {
            get
            {
                lock (_gate) return [.. _slots.Select((_, index) => _items[index]!.Value)];
            }
        }
    }

    private sealed record BatchTarget(BatchCollector Collector, int Slot);
}
