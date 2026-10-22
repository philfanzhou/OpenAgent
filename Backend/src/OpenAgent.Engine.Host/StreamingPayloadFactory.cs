using System.ClientModel;
using System.Net.Http;
using System.Net.Sockets;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Application.Runtime;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host;

internal static class StreamingPayloadFactory
{
    private const string TypePrefix = "https://error.agent.com";
    private const string ContextOverflowType = "https://error.agent.com/context-length-exceeded";

    public static StreamingErrorPayload CreateErrorPayload(
        Exception exception,
        string traceId)
    {
        // 上下文超限优先于一切通用映射：provider 原文对用户无行动价值，且异常常被
        // 包装，必须按 InnerException 链识别，而不是只认 ClientResultException。
        // Runtime 分类器产出的 LlmContextOverflow 文案同样命中链式标记，一并覆盖。
        ClientResultException? providerException = FindClientResultException(exception);
        if (ExecutionFailureDescriptor.IsContextLengthExceeded(exception))
        {
            return new StreamingErrorPayload
            {
                Type = ContextOverflowType,
                Title = ExecutionFailureDescriptor.ContextOverflowTitle,
                Detail = ExecutionFailureDescriptor.BuildContextOverflowDetail(
                    providerException?.Status,
                    providerException?.Message),
                Code = nameof(AgentErrorCode.LlmContextOverflow),
                TraceId = traceId
            };
        }

        // Runtime 分类错误（AgentException）与 provider 错误：与持久化元数据共用
        // Describe 的脱敏文案——SSE error 事件和刷新后的消息 Error 不再各说各话。
        if (exception is AgentException or ClientResultException
            && ExecutionFailureDescriptor.Describe(exception, traceId) is { } described)
        {
            return new StreamingErrorPayload
            {
                Type = exception is AgentException agent
                    ? $"{TypePrefix}/{agent.ErrorCode.ToString().ToLowerInvariant()}"
                    : $"{TypePrefix}/provider-request-error",
                Title = described.Title,
                Detail = described.Detail,
                Code = described.Code,
                TraceId = traceId
            };
        }

        var message = exception switch
        {
            TimeoutException or OperationCanceledException => "请求超时，服务暂不可用",
            InvalidOperationException => exception.Message,
            HttpRequestException httpEx when httpEx.InnerException is SocketException sockEx && sockEx.SocketErrorCode == SocketError.TryAgain => "网络连接失败，请检查网络配置",
            HttpRequestException => "服务请求失败",
            // 未知异常原文可能包含内部细节，只给通用文案；原文随服务器日志按 TraceId 留痕。
            _ => ExecutionFailureDescriptor.GenericDetail
        };

        return new StreamingErrorPayload
        {
            Type = exception switch
            {
                TimeoutException or OperationCanceledException => "https://error.agent.com/timeout",
                InvalidOperationException => "https://error.agent.com/configuration-error",
                HttpRequestException httpEx when httpEx.InnerException is SocketException sockEx && sockEx.SocketErrorCode == SocketError.TryAgain => "https://error.agent.com/dns-resolution-error",
                HttpRequestException => "https://error.agent.com/http-request-error",
                _ => "https://error.agent.com/internal-error"
            },
            Title = exception switch
            {
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

    private static ClientResultException? FindClientResultException(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is ClientResultException provider)
            {
                return provider;
            }
        }
        return null;
    }
}
