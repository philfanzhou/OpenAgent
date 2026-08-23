using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Engine.Host;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentStreamWriter
{
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task WriteSseStreamAsync(
        HttpContext context,
        IAsyncEnumerable<AgentStreamEvent> events,
        string traceId,
        string conversationId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        StreamingResponseHeaders.ApplySse(context);
        TokenUsage? usage = null;
        string? modelId = null;
        bool awaitingApproval = false;
        await using StreamingHeartbeat heartbeat = StreamingHeartbeat.Start(
            token => WriteHeartbeatAsync(context, token),
            StreamHeartbeatInterval,
            logger,
            "agent-stream",
            traceId,
            cancellationToken);
        // 尽早下发 conversationId：客户端在流式中止（如暂停/停止）时也能获知真实会话 ID，
        // 否则首次会话被中止后前端不知道会话 ID，后续输入会被当成新会话。
        await WriteSseEventAsync(
            context,
            "conversation",
            JsonSerializer.Serialize(new { conversationId }, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        await foreach (AgentStreamEvent streamEvent in events.WithCancellation(cancellationToken))
        {
            if (streamEvent.Type == AgentStreamEventType.Usage)
            {
                usage = streamEvent.Usage;
                modelId = streamEvent.ModelId;
                continue;
            }
            awaitingApproval |= streamEvent.Type == AgentStreamEventType.Approval;

            string eventName = streamEvent.Type switch
            {
                AgentStreamEventType.Reasoning => "reasoning",
                AgentStreamEventType.ToolCall => "tool_call",
                AgentStreamEventType.Approval => "approval",
                _ => "content"
            };
            string data = JsonSerializer.Serialize(new
            {
                content = streamEvent.Content,
                toolName = streamEvent.ToolName,
                toolCallId = streamEvent.ToolCallId,
                toolArguments = streamEvent.ToolArguments,
                approval = streamEvent.Approval
            }, JsonOptions);
            await heartbeat.WriteAsync(
                token => WriteSseEventAsync(context, eventName, data, token),
                cancellationToken).ConfigureAwait(false);
        }

        string done = JsonSerializer.Serialize(
            new
            {
                done = true,
                usage,
                modelId,
                conversationId,
                status = awaitingApproval ? "AwaitingApproval" : "Completed"
            },
            JsonOptions);
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
