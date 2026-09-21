using System.ClientModel;
using OpenAgent.Contracts.Requests;
using OpenAgent.Hosting;
using OpenAgent.Hosting.Errors;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Engine.Host;

/// <summary>
/// Engine 特有的错误处理接入：注册 OpenAI/Azure SDK 的 ClientResultException 映射与
/// 中文 SSE 错误文案。核心映射、ProblemDetails 构造与全局异常中间件在
/// OpenAgent.Hosting.Errors 中与其他服务共享。
/// </summary>
internal static class EngineErrorHandling
{
    internal static IServiceCollection AddEngineErrorHandling(this IServiceCollection services) =>
        services.AddAgentErrorHandling(options =>
        {
            options.ExtraExceptionMappers.Add(MapClientResultException);
            options.CreateStreamingErrorPayload = (exception, traceId, _) =>
                StreamingPayloadFactory.CreateErrorPayload(exception, traceId);
        });

    private static AgentMappedError? MapClientResultException(Exception exception, string traceId)
    {
        if (exception is not ClientResultException clientException)
        {
            return null;
        }

        int statusCode = clientException.Status is >= 400 and < 500 ? clientException.Status : 502;
        ProblemDetails problem = AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/provider-request-error",
            "ProviderRequestFailed",
            statusCode,
            StreamingPayloadFactory.FormatProviderError(clientException.Status, clientException.Message),
            clientException.Message,
            traceId,
            AgentErrorCode.DependencyUnavailable);
        return new AgentMappedError(statusCode, problem);
    }
}
