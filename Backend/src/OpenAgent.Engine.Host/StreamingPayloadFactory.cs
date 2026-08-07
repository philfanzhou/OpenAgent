using System.Net.Http;
using System.Net.Sockets;
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

}

internal sealed class StreamingErrorPayload
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string TraceId { get; init; }
}
