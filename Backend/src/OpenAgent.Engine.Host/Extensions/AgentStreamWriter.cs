using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Engine.Host;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentStreamWriter
{
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(15);

    internal static async Task WriteNdjsonStreamAsync(
        HttpContext context,
        IAsyncEnumerable<AgentStreamEvent> events,
        string traceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        StreamingResponseHeaders.ApplyNdjson(context);
        TokenUsage? usage = null;
        try
        {
            await using StreamingHeartbeat heartbeat = StreamingHeartbeat.Start(
                token => WriteNdjsonHeartbeatAsync(context, traceId, token),
                StreamHeartbeatInterval,
                logger,
                "agent-stream",
                traceId,
                cancellationToken);
            await foreach (AgentStreamEvent streamEvent in events.WithCancellation(cancellationToken))
            {
                if (streamEvent.Type == AgentStreamEventType.Usage)
                {
                    usage = streamEvent.Usage;
                    continue;
                }

                await heartbeat.WriteAsync(
                    token => WriteNdjsonEventAsync(
                        context,
                        StreamingPayloadFactory.CreateAgentEvent(streamEvent, traceId),
                        token),
                    cancellationToken).ConfigureAwait(false);
            }

            await heartbeat.WriteAsync(
                token => WriteNdjsonEventAsync(
                    context,
                    StreamingPayloadFactory.CreateDoneEvent(traceId, usage: usage),
                    token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteNdjsonEventAsync(
                context,
                StreamingPayloadFactory.CreateDoneEvent(traceId, "cancelled"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteNdjsonEventAsync(
                context,
                StreamingPayloadFactory.CreateErrorEvent(
                    StreamingPayloadFactory.CreateErrorPayload(exception, traceId),
                    traceId),
                CancellationToken.None).ConfigureAwait(false);
            await WriteNdjsonEventAsync(
                context,
                StreamingPayloadFactory.CreateDoneEvent(traceId, "error"),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static async Task WriteNdjsonEventAsync(
        HttpContext context,
        NdjsonStreamEvent payload,
        CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(payload);
        await context.Response.WriteAsync(line + "\n", cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteNdjsonHeartbeatAsync(
        HttpContext context,
        string traceId,
        CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(new { type = "heartbeat", traceId });
        await context.Response.WriteAsync(line + "\n", cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
