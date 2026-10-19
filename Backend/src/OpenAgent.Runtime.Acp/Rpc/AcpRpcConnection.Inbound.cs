using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Protocol.Serialization;

namespace OpenAgent.Runtime.Acp.Rpc;

public sealed partial class AcpRpcConnection
{
    private async Task RunReaderAsync()
    {
        while (true)
        {
            byte[]? frame;
            try
            {
                frame = await _transport.ReadAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Close("the transport reader failed", exception is OperationCanceledException ? null : exception);
                return;
            }

            if (frame is null)
            {
                Close("the peer closed the stream (EOF)");
                return;
            }

            await ProcessFrameAsync(frame).ConfigureAwait(false);
        }
    }

    private async Task ProcessFrameAsync(byte[] frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException exception)
        {
            await WriteErrorResponseAsync(null, ErrorCode.ParseError, "The message is not valid JSON.", exception).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                await ProcessBatchAsync(document.RootElement).ConfigureAwait(false);
                return;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                await WriteErrorResponseAsync(null, ErrorCode.InvalidRequest, "The message must be an object or a batch.").ConfigureAwait(false);
                return;
            }

            // A single message resolves inline; only batch responses assemble out of band.
            Task? inline = ProcessMessageAsync(document.RootElement, collector: null);
            if (inline is not null) await inline.ConfigureAwait(false);
        }
    }

    private async Task ProcessBatchAsync(JsonElement batch)
    {
        if (batch.GetArrayLength() == 0)
        {
            await WriteErrorResponseAsync(null, ErrorCode.InvalidRequest, "A batch must not be empty.").ConfigureAwait(false);
            return;
        }

        BatchCollector collector = new();
        // Registered before any dispatch: shutdown can then settle unfinished slots even when
        // the assembly task has not been tracked yet, so no collector is ever missed.
        _activeBatches[collector] = 0;
        foreach (JsonElement item in batch.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                collector.Fail(collector.NextSlot(), null, new Error(ErrorCode.InvalidRequest, "A batch item must be an object."));
                continue;
            }

            // Inline tasks only cover routing and rejection bookkeeping; handlers run detached.
            Task? inline = ProcessMessageAsync(item, collector);
            if (inline is not null) await inline.ConfigureAwait(false);
        }

        collector.Seal();
        _ = Track(AssembleWhenDoneAsync(collector));
    }

    /// <summary>Routes one wire message. Returns the inline completion task for single messages; batch responses resolve through the collector.</summary>
    private Task? ProcessMessageAsync(JsonElement message, BatchCollector? collector)
    {
        if (!message.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            if (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _))
            {
                HandleResponse(message);
                return null;
            }

            RpcId? id = message.TryGetProperty("id", out JsonElement present) ? ExtractEchoId(present) : null;
            return collector is not null
                ? FailInBatch(collector, id, ErrorCode.InvalidRequest, "A batch item is neither request, notification, nor response.")
                : WriteErrorResponseAsync(id, ErrorCode.InvalidRequest, "The message is neither request, notification, nor response.");
        }

        string method = methodElement.GetString()!;
        if (method == CancelRequestMethod) return HandleCancelNotificationAsync(message, collector);

        bool hasIdProperty = message.TryGetProperty("id", out JsonElement rawId);
        if (hasIdProperty && rawId.ValueKind is JsonValueKind.Null)
        {
            // An explicit id:null is neither a request nor a notification; the cases stay distinct.
            return collector is null
                ? WriteErrorResponseAsync(RpcId.Null, ErrorCode.InvalidRequest, "A request ID must be a string or an integer; null IDs are not requests.")
                : FailInBatch(collector, null, ErrorCode.InvalidRequest, "A request ID must be a string or an integer; null IDs are not requests.");
        }

        if (hasIdProperty) return HandleRequestAsync(message, method, rawId, collector);

        HandleNotificationAsync(message, method);
        return null;
    }

    private async Task HandleRequestAsync(JsonElement message, string method, JsonElement rawId, BatchCollector? collector)
    {
        RpcId? echo = ExtractEchoId(rawId);
        if (echo is null)
        {
            await FailRequestAsync(null, method, collector, ErrorCode.InvalidRequest, "A request ID must be a string or an integer; null IDs are not requests.").ConfigureAwait(false);
            return;
        }

        RpcId id = echo.GetValueOrDefault();
        try
        {
            if (IsClosed)
            {
                await FailRequestAsync(id, method, collector, ErrorCode.InternalError, "The connection is closing.").ConfigureAwait(false);
                return;
            }

            if (_catalog.TryGetValue(method, out AcpMethodDescriptor? descriptor))
            {
                string inboundDirection = _role == AcpRpcRole.Client ? "agent_to_client" : "client_to_agent";
                if (descriptor.Direction != "bidirectional" && descriptor.Direction != inboundDirection)
                {
                    // A method the pinned catalog defines only for the other direction is method-not-found here.
                    await FailRequestAsync(id, method, collector, ErrorCode.MethodNotFound, $"The '{method}' method is not served by this endpoint.").ConfigureAwait(false);
                    return;
                }

                if (descriptor.Kind == "notification")
                {
                    await FailRequestAsync(id, method, collector, ErrorCode.InvalidRequest, $"The '{method}' method is a notification and carries no response.").ConfigureAwait(false);
                    return;
                }
            }

            if (!_requestHandlers.TryGetValue(method, out RegisteredHandler? registered))
            {
                await FailRequestAsync(id, method, collector, ErrorCode.MethodNotFound, $"No handler is registered for '{method}'.").ConfigureAwait(false);
                return;
            }

            // Envelope read validates the payload per the in-band method; failures are Invalid Params.
            _ = JsonSerializer.Deserialize(message, EnvelopeInfo(_inboundRequestEnvelope));

            JsonElement? parameters = message.TryGetProperty("params", out JsonElement paramsElement) ? paramsElement : null;
            if (Interlocked.Increment(ref _activeHandlers) > _options.MaxConcurrentHandlers)
            {
                Interlocked.Decrement(ref _activeHandlers);
                await FailRequestAsync(id, method, collector, ErrorCode.InternalError, "The handler task quota is exhausted.",
                    data: BuildElement(writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("limit", nameof(AcpRpcOptions.MaxConcurrentHandlers));
                        writer.WriteEndObject();
                    })).ConfigureAwait(false);
                return;
            }

            // Handlers outlive the frame's JsonDocument, so the payload is cloned at the boundary
            // (PROTOCOL §6: payloads stay valid after the source document is released).
            AcpRpcRequest request = new(id, method, parameters is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } ? parameters.Value.Clone() : null);
            // The slot is reserved on the reader thread, so batch responses keep their original item order.
            BatchTarget? target = collector is null ? null : new BatchTarget(collector, collector.NextSlot());
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _inflight[PendingKey(id)] = cancellation;
            _ = Track(DispatchRequestAsync(request, registered, target, cancellation));
        }
        catch (JsonException)
        {
            await FailRequestAsync(id, method, collector, ErrorCode.InvalidParams, "The request parameters do not match the method schema.").ConfigureAwait(false);
        }
    }

    private async Task DispatchRequestAsync(AcpRpcRequest request, RegisteredHandler registered, BatchTarget? target, CancellationTokenSource cancellation)
    {
        try
        {
            JsonElement? result = await registered.Handler(request, cancellation.Token).ConfigureAwait(false);
            await WriteRequestResponseAsync(request.Id, request.Method, result, error: null, target).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            await WriteRequestResponseAsync(request.Id, request.Method, null, new Error(ErrorCode.RequestCancelled, "The request was cancelled."), target).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Shutdown writes no frame on a closing link, but the batch slot must still settle:
            // an unfilled slot would park the batch assembly task and hang disposal forever.
            if (target is not null)
            {
                target.Collector.Fail(target.Slot, request.Id, new Error(ErrorCode.InternalError, "The connection is closing."));
            }
        }
        catch (AcpRpcException exception)
        {
            await WriteRequestResponseAsync(request.Id, request.Method, null, exception.Error, target).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            Diagnostic(AcpRpcDiagnosticKind.HandlerFault, request.Method, request.Id, exception.GetType().Name);
            await WriteRequestResponseAsync(request.Id, request.Method, null, new Error(ErrorCode.InternalError, "The handler produced an invalid protocol result."), target).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Diagnostic(AcpRpcDiagnosticKind.HandlerFault, request.Method, request.Id, exception.GetType().Name);
            await WriteRequestResponseAsync(request.Id, request.Method, null, new Error(ErrorCode.InternalError, "The handler failed."), target).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeHandlers);
            if (_inflight.TryRemove(KeyValuePair.Create(PendingKey(request.Id), cancellation)))
            {
                cancellation.Dispose();
            }
        }
    }

    /// <summary>Completes one inbound request: validates the payload against its method schema, then queues the frame or the batch slot.</summary>
    private async Task WriteRequestResponseAsync(RpcId id, string? requestMethod, JsonElement? result, Error? error, BatchTarget? target)
    {
        try
        {
            if ((result is null) == (error is null)) throw new InvalidOperationException("A response carries exactly one of result and error.");
            JsonElement response = BuildResponseObject(id, result, error);
            if (result is not null && requestMethod is not null) ValidateResult(requestMethod, result.Value);
            JsonElement validated = SchemaValidator.Process(_outboundResponseEnvelope, response, compatibility: false);
            if (target is null)
            {
                await EnqueueFrameAsync(ToFrame(validated)).ConfigureAwait(false);
            }
            else
            {
                target.Collector.Complete(target.Slot, id, validated);
            }
        }
        catch (Exception exception)
        {
            Diagnostic(AcpRpcDiagnosticKind.HandlerFault, requestMethod, id, exception.GetType().Name);
            // Never write a malformed response; answer Internal Error so the peer is not left waiting.
            try
            {
                JsonElement fallback = SchemaValidator.Process(
                    _outboundResponseEnvelope,
                    BuildResponseObject(id, null, new Error(ErrorCode.InternalError, "The response could not be built.")),
                    compatibility: false);
                if (target is null) await EnqueueFrameAsync(ToFrame(fallback)).ConfigureAwait(false);
                else target.Collector.Fail(target.Slot, id, new Error(ErrorCode.InternalError, "The response could not be built."));
            }
            catch (Exception inner)
            {
                Close("a response could not be written", inner);
            }
        }
    }

    private Task FailInBatch(BatchCollector collector, RpcId? id, int code, string message)
    {
        collector.Fail(collector.NextSlot(), id, new Error(code, message));
        Diagnostic(AcpRpcDiagnosticKind.RejectedRequest, null, id, message);
        return Task.CompletedTask;
    }

    private async Task FailRequestAsync(RpcId? id, string? method, BatchCollector? collector, int code, string message, Exception? exception = null, JsonElement? data = null)
    {
        if (collector is not null)
        {
            collector.Fail(collector.NextSlot(), id, data is null ? new Error(code, message) : new Error(code, message, Optional<JsonElement>.FromValue(data.Value)));
        }
        else
        {
            await WriteErrorResponseAsync(id, code, message, exception, data).ConfigureAwait(false);
        }

        Diagnostic(AcpRpcDiagnosticKind.RejectedRequest, method, id, message);
    }

    private void HandleNotificationAsync(JsonElement message, string method)
    {
        if (_catalog.TryGetValue(method, out AcpMethodDescriptor? descriptor) && descriptor.Kind == "request")
        {
            Diagnostic(AcpRpcDiagnosticKind.UnhandledNotification, method, null, "request-kind method used as a notification");
            return;
        }

        JsonElement? parameters = message.TryGetProperty("params", out JsonElement paramsElement) ? paramsElement : null;
        try
        {
            _ = JsonSerializer.Deserialize(message, EnvelopeInfo(_inboundNotificationEnvelope));
        }
        catch (JsonException exception)
        {
            Diagnostic(AcpRpcDiagnosticKind.UnhandledNotification, method, null, "malformed notification dropped: " + exception.GetType().Name);
            return;
        }

        if (!_notificationHandlers.TryGetValue(method, out Func<AcpRpcNotification, CancellationToken, Task>? handler))
        {
            Diagnostic(AcpRpcDiagnosticKind.UnhandledNotification, method, null, "no handler registered");
            return;
        }

        // The queued notification outlives the frame's JsonDocument; clone the payload at the boundary.
        AcpRpcNotification notification = new(method, parameters is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } ? parameters.Value.Clone() : null);
        if (!_notifications.Writer.TryWrite(notification))
        {
            Close($"the notification buffer exceeded {nameof(AcpRpcOptions.MaxQueuedNotifications)}",
                new AcpQuotaExceededException(nameof(AcpRpcOptions.MaxQueuedNotifications), _options.MaxQueuedNotifications, _options.MaxQueuedNotifications + 1L));
            return;
        }

        Interlocked.Increment(ref _notificationsQueued);
    }

    private async Task RunNotificationLoopAsync()
    {
        try
        {
            await foreach (AcpRpcNotification notification in _notifications.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    if (_notificationHandlers.TryGetValue(notification.Method, out Func<AcpRpcNotification, CancellationToken, Task>? handler))
                    {
                        await handler(notification, _lifetime.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Diagnostic(AcpRpcDiagnosticKind.HandlerFault, notification.Method, null, exception.GetType().Name);
                }
                finally
                {
                    // One increment per consumed item, handled or not: drain waiters measure queue transit.
                    SignalNotificationDispatch();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception exception)
        {
            Close("the notification dispatcher failed", exception);
        }
    }

    /// <summary>Intercepts $/cancel_request at the reader: cancelling never waits behind queued notifications or slow handlers.</summary>
    private Task HandleCancelNotificationAsync(JsonElement message, BatchCollector? collector)
    {
        bool hasId = message.TryGetProperty("id", out JsonElement rawId) && rawId.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (hasId)
        {
            // The cancel notification carries no response; using it as a request is invalid usage.
            RpcId? id = ExtractEchoId(rawId);
            return collector is not null
                ? FailInBatch(collector, id, ErrorCode.InvalidRequest, "The $/cancel_request method is a notification.")
                : WriteErrorResponseAsync(id, ErrorCode.InvalidRequest, "The $/cancel_request method is a notification.");
        }

        if (!message.TryGetProperty("params", out JsonElement paramsElement) || paramsElement.ValueKind != JsonValueKind.Object)
        {
            Diagnostic(AcpRpcDiagnosticKind.UnmatchedCancellation, CancelRequestMethod, null, "malformed cancel notification dropped");
            return Task.CompletedTask;
        }

        RpcId requestId;
        try
        {
            requestId = AcpJson.FromElement<CancelRequestNotification>(paramsElement).RequestId;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            Diagnostic(AcpRpcDiagnosticKind.UnmatchedCancellation, CancelRequestMethod, null, "malformed cancel notification dropped");
            return Task.CompletedTask;
        }

        // 1. Our own outbound call the peer is cancelling: keep waiting for its final response, bounded by the grace period.
        if (_pending.TryGetValue(PendingKey(requestId), out PendingCall? call))
        {
            StartCancelFlow(call, sendNotification: false);
            return Task.CompletedTask;
        }

        // 2. A running inbound handler to cancel cooperatively.
        if (_inflight.TryGetValue(PendingKey(requestId), out CancellationTokenSource? cancellation))
        {
            return cancellation.CancelAsync();
        }

        Diagnostic(AcpRpcDiagnosticKind.UnmatchedCancellation, CancelRequestMethod, requestId, "no matching pending call or handler");
        return Task.CompletedTask;
    }

    private void HandleResponse(JsonElement message)
    {
        ProtocolResponse response;
        try
        {
            response = ProtocolResponse.Parse((AcpModel)JsonSerializer.Deserialize(message, EnvelopeInfo(_inboundResponseEnvelope))!);
        }
        catch (JsonException exception)
        {
            Diagnostic(AcpRpcDiagnosticKind.MalformedResponse, null, null, "malformed response dropped: " + exception.GetType().Name);
            return;
        }

        if (response.Id.IsNull)
        {
            Diagnostic(AcpRpcDiagnosticKind.MalformedResponse, null, null, "response without a correlatable ID");
            return;
        }

        RpcId responseId = response.Id;
        PendingCall? call = _pending.GetValueOrDefault(PendingKey(responseId));
        if (call is null)
        {
            // A recently answered ID is a duplicate; anything else never belonged to this connection.
            string responseKey = PendingKey(responseId);
            bool duplicate = _recentlyCompletedSet.ContainsKey(responseKey);
            Diagnostic(duplicate ? AcpRpcDiagnosticKind.DuplicateResponse : AcpRpcDiagnosticKind.UnmatchedResponse,
                null, responseId, duplicate ? "duplicate response for a completed call" : "no pending call for this ID");
            return;
        }

        if (response.Error is not null)
        {
            // The error object aliases the frame document; callers read it after the frame is released.
            Error error = response.Error;
            Optional<JsonElement> data = !error.Data.IsDefined ? default
                : error.Data.IsNull ? Optional<JsonElement>.Null
                : Optional<JsonElement>.FromValue(error.Data.Value.Clone());
            CompletePending(call, null, new Error(error.Code, error.Message, data));
            return;
        }

        // Per-method validation of the verbatim result, selected by the pending request's method (PROTOCOL §6).
        try
        {
            JsonElement validated = response.Result is null
                ? BuildElement(static writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                })
                : ValidateResult(call.Method, response.Result.Value);
            // The awaiting caller reads the result after the frame document is released; keep an owned copy.
            CompletePending(call, validated.Clone(), null);
        }
        catch (JsonException)
        {
            FailPending(call, new JsonException($"The '{call.Method}' result does not match its response schema."));
            Diagnostic(AcpRpcDiagnosticKind.MalformedResponse, call.Method, response.Id, "result failed the method schema");
        }
    }

    private JsonElement ValidateResult(string method, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) throw new JsonException("A protocol result must be a JSON object.");
        return _catalog.TryGetValue(method, out AcpMethodDescriptor? descriptor) && descriptor.ResponseSchema is { } schema
            ? SchemaValidator.Process(schema, result, compatibility: false)
            : result;
    }

    /// <summary>Assembles and writes one response array for a fully completed batch, in original item order.</summary>
    private async Task AssembleWhenDoneAsync(BatchCollector collector)
    {
        try
        {
            await collector.CollectAsync().ConfigureAwait(false);
            // A closing link owns the wire; the sweep already settled every slot, so nothing is written.
            if (_lifetime.IsCancellationRequested) return;
            BatchItem[] items = collector.Items;
            if (items.Length == 0) return;
            JsonElement[] responses = new JsonElement[items.Length];
            for (int index = 0; index < items.Length; index++)
            {
                BatchItem item = items[index];
                // Success slots already hold the envelope-validated response object; error slots still need one built.
                responses[index] = item.Error is null
                    ? item.Result!.Value
                    : SchemaValidator.Process(_outboundResponseEnvelope, BuildResponseObject(item.Id ?? RpcId.Null, null, item.Error), compatibility: false);
            }

            JsonElement batch = BuildElement(writer =>
            {
                writer.WriteStartArray();
                foreach (JsonElement response in responses) response.WriteTo(writer);
                writer.WriteEndArray();
            });
            await EnqueueFrameAsync(ToFrame(batch)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Close("a batch response could not be assembled", exception);
        }
        finally
        {
            _activeBatches.TryRemove(collector, out _);
        }
    }

    private JsonTypeInfo EnvelopeInfo(string schemaName) => schemaName switch
    {
        "ClientRequest" => AcpJson.TypeInfo<ClientRequest>(),
        "AgentRequest" => AcpJson.TypeInfo<AgentRequest>(),
        "ClientNotification" => AcpJson.TypeInfo<ClientNotification>(),
        "AgentNotification" => AcpJson.TypeInfo<AgentNotification>(),
        "ClientResponse" => AcpJson.TypeInfo<ClientResponse>(),
        _ => AcpJson.TypeInfo<AgentResponse>()
    };

    private static RpcId? ExtractEchoId(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new RpcId(element.GetString()!),
        JsonValueKind.Number when element.TryGetInt64(out long number) => new RpcId(number),
        _ => (RpcId?)null
    };

    private sealed class ProtocolResponse
    {
        private ProtocolResponse(RpcId id, JsonElement? result, Error? error)
        {
            Id = id;
            Result = result;
            Error = error;
        }

        public RpcId Id { get; }
        public JsonElement? Result { get; }
        public Error? Error { get; }

        public static ProtocolResponse Parse(AcpModel envelope)
        {
            JsonElement json = envelope.Json;
            RpcId id = json.GetProperty("id").Deserialize(AcpJson.TypeInfo<RpcId>()) is { } parsed ? parsed : RpcId.Null;
            if (json.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.Object)
            {
                return new ProtocolResponse(id, null, new Error(errorElement, true));
            }

            return new ProtocolResponse(id, json.TryGetProperty("result", out JsonElement result) ? result : null, null);
        }
    }

    private readonly record struct BatchItem(RpcId? Id, Error? Error, JsonElement? Result);
}
