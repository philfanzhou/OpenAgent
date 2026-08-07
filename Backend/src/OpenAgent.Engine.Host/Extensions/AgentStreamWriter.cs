using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Engine.Host;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentStreamWriter
{
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(15);

    internal static async Task WriteSseStreamAsync(
        HttpContext context,
        IAsyncEnumerable<AgentStreamEvent> events,
        string traceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        StreamingResponseHeaders.ApplySse(context);
        TokenUsage? usage = null;
        await using StreamingHeartbeat heartbeat = StreamingHeartbeat.Start(
            token => WriteHeartbeatAsync(context, token),
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

            string eventName = streamEvent.Type switch
            {
                AgentStreamEventType.Reasoning => "reasoning",
                AgentStreamEventType.ToolCall => "tool_call",
                _ => "content"
            };
            string data = JsonSerializer.Serialize(new
            {
                content = streamEvent.Content,
                toolName = streamEvent.ToolName,
                toolCallId = streamEvent.ToolCallId
            });
            await heartbeat.WriteAsync(
                token => WriteSseEventAsync(context, eventName, data, token),
                cancellationToken).ConfigureAwait(false);
        }

        string done = JsonSerializer.Serialize(new { done = true, usage });
        await WriteSseEventAsync(context, "done", done, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteHeartbeatAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync(": heartbeat\n\n", cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSseEventAsync(
        HttpContext context,
        string eventName,
        string data,
        CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync(
            $"event: {eventName}\ndata: {data}\n\n",
            cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
