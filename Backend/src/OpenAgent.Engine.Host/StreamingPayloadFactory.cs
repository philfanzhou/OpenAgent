using System.ClientModel;
using System.Net.Http;
using System.Net.Sockets;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Host;

internal static class StreamingPayloadFactory
{
    public static StreamingErrorPayload CreateErrorPayload(
        Exception exception,
        string traceId)
    {
        var message = exception switch
        {
            AgentException ae => ae.Message,
            ClientResultException clientEx => FormatProviderError(clientEx.Status, clientEx.Message),
            TimeoutException or OperationCanceledException => "请求超时，服务暂不可用",
            InvalidOperationException => exception.Message,
            HttpRequestException httpEx when httpEx.InnerException is SocketException sockEx && sockEx.SocketErrorCode == SocketError.TryAgain => "网络连接失败，请检查网络配置",
            HttpRequestException => "服务请求失败",
            _ => string.IsNullOrWhiteSpace(exception.Message) ? "服务内部错误" : exception.Message
        };

        return new StreamingErrorPayload
        {
            Type = exception switch
            {
                AgentException ae => $"https://error.agent.com/{ae.ErrorCode.ToString().ToLowerInvariant()}",
                ClientResultException => "https://error.agent.com/provider-request-error",
                TimeoutException or OperationCanceledException => "https://error.agent.com/timeout",
                InvalidOperationException => "https://error.agent.com/configuration-error",
                HttpRequestException httpEx when httpEx.InnerException is SocketException sockEx && sockEx.SocketErrorCode == SocketError.TryAgain => "https://error.agent.com/dns-resolution-error",
                HttpRequestException => "https://error.agent.com/http-request-error",
                _ => "https://error.agent.com/internal-error"
            },
            Title = exception switch
            {
                AgentException ae => ae.ErrorCode.ToString(),
                ClientResultException => "模型服务返回错误",
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

    internal static string FormatProviderError(int status, string message)
    {
        string type = string.Empty;
        string detail = string.Empty;
        if (!string.IsNullOrWhiteSpace(message))
        {
            int open = message.LastIndexOf('(');
            int close = message.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                string inner = message.Substring(open + 1, close - open - 1);
                int colon = inner.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    type = inner[..colon].Trim();
                    detail = inner[(colon + 2)..].Trim();
                }
                else
                {
                    type = inner.Trim();
                }
            }
        }

        string core = string.IsNullOrWhiteSpace(type) ? $"HTTP {status}" : $"HTTP {status} · {type}";
        return string.IsNullOrWhiteSpace(detail)
            ? $"模型服务返回错误（{core}）。请检查模型配置后重试。"
            : $"模型服务返回错误（{core}）：{detail}";
    }
}

internal sealed class StreamingErrorPayload
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string TraceId { get; init; }
}
