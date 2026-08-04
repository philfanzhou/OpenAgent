using System.Net.Http;
using System.Net.Sockets;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Host;

internal static class StreamingPayloadFactory
{
    public static StreamingErrorPayload CreateErrorPayload(Exception exception, string traceId)
    {
        var message = exception switch
        {
            AgentException ae => ae.Message,
            TimeoutException or OperationCanceledException => "请求超时，服务暂不可用",
            InvalidOperationException => exception.Message,
            HttpRequestException httpEx when httpEx.InnerException is SocketException sockEx && sockEx.SocketErrorCode == SocketError.TryAgain => "网络连接失败，请检查网络配置",
            HttpRequestException => "服务请求失败",
            _ => "服务内部错误"
        };

        return new StreamingErrorPayload
        {
            Type = exception switch
            {
                AgentException ae => $"https://error.agent.com/{ae.ErrorCode.ToString().ToLowerInvariant()}",
                TimeoutException or OperationCanceledException => "https://error.agent.com/timeout",
                InvalidOperationException => "https://error.agent.com/configuration-error",
                HttpRequestException httpEx when httpEx.InnerException is SocketException sockEx && sockEx.SocketErrorCode == SocketError.TryAgain => "https://error.agent.com/dns-resolution-error",
                HttpRequestException => "https://error.agent.com/http-request-error",
                _ => "https://error.agent.com/internal-error"
            },
            Title = exception switch
            {
                AgentException ae => ae.ErrorCode.ToString(),
                TimeoutException or OperationCanceledException => "GatewayTimeout",
                InvalidOperationException => "ConfigurationError",
                HttpRequestException httpEx when httpEx.InnerException is SocketException => "DnsResolutionError",
                HttpRequestException => "HttpRequestError",
                _ => "InternalServerError"
            },
            Detail = message,
            TraceId = traceId
        };
    }

    public static NdjsonStreamEvent CreateContentEvent(string content, string traceId)
    {
        return new NdjsonStreamEvent
        {
            Type = "content",
            Content = content,
            TraceId = traceId
        };
    }

    public static NdjsonStreamEvent CreateAgentEvent(AgentStreamEvent streamEvent, string traceId)
    {
        return new NdjsonStreamEvent
        {
            Type = streamEvent.Type switch
            {
                AgentStreamEventType.Reasoning => "reasoning",
                AgentStreamEventType.ToolCall => "tool_call",
                _ => "content"
            },
            Content = streamEvent.Content,
            ToolName = streamEvent.ToolName,
            ToolCallId = streamEvent.ToolCallId,
            TraceId = traceId
        };
    }

    public static NdjsonStreamEvent CreateErrorEvent(StreamingErrorPayload error, string traceId)
    {
        return new NdjsonStreamEvent
        {
            Type = "error",
            Error = error,
            TraceId = traceId
        };
    }

    public static NdjsonStreamEvent CreateDoneEvent(string traceId, string status = "completed", TokenUsage? usage = null)
    {
        return new NdjsonStreamEvent
        {
            Type = "done",
            Status = status,
            TraceId = traceId,
            Usage = usage
        };
    }
}

internal sealed class StreamingErrorPayload
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string TraceId { get; init; }
}

internal sealed class NdjsonStreamEvent
{
    public required string Type { get; init; }
    public string? Content { get; init; }
    public string? Status { get; init; }
    public string? TraceId { get; init; }
    public StreamingErrorPayload? Error { get; init; }
    public TokenUsage? Usage { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
}
