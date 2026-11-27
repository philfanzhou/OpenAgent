using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Engine.Hosting.Errors;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// 转发失败时的降级响应：非流式请求返回统一 ProblemDetails；流式请求在响应尚未开始时
/// 发 error/done 事件。Engine 的聊天流式端点只有 SSE（<c>/chat/stream</c>），没有 NDJSON 端点，
/// 所以所有流式动作都写 SSE，载荷与 Engine 的失败帧同形。
/// </summary>
internal static class StreamingForwardingError
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<IResult> HandleAsync(
        HttpContext context,
        string? action,
        ForwarderError error,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (!IsStreamingAction(action))
        {
            return CreateProblemFallback(error, traceId, context);
        }

        if (context.Response.HasStarted)
        {
            return Results.Empty;
        }

        try
        {
            await WriteSseErrorAsync(context, error, traceId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Results.Empty;
        }
        catch (IOException)
        {
            return Results.Empty;
        }

        return Results.Empty;
    }

    private static bool IsStreamingAction(string? action)
    {
        return string.Equals(action, "sse", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "stream", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult CreateProblemFallback(ForwarderError error, string traceId, HttpContext context)
    {
        if (error == ForwarderError.RequestTimedOut)
        {
            return TypedResults.Problem(AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/gateway-timeout",
                "GatewayTimeout",
                StatusCodes.Status504GatewayTimeout,
                "The request to the AI engine timed out. Please try again later.",
                context.Request.Path,
                traceId,
                ("errorCode", (int)AgentErrorCode.DependencyUnavailable)));
        }

        return TypedResults.Problem(AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/service-unavailable",
            "ServiceUnavailable",
            StatusCodes.Status503ServiceUnavailable,
            "The AI engine is temporarily unavailable.",
            context.Request.Path,
            traceId,
            ("errorCode", (int)AgentErrorCode.DependencyUnavailable)));
    }

    private static async Task WriteSseErrorAsync(
        HttpContext context,
        ForwarderError error,
        string traceId,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        var payload = JsonSerializer.Serialize(new
        {
            type = $"{AgentProblemDetails.TypePrefix}/forwarding-failed",
            title = error == ForwarderError.RequestTimedOut ? "GatewayTimeout" : "ServiceUnavailable",
            detail = $"Forwarding failed: {error}",
            traceId
        }, JsonOptions);
        var done = JsonSerializer.Serialize(new { done = true, status = "error" }, JsonOptions);

        await context.Response.WriteAsync($"event: error\ndata: {payload}\n\n", cancellationToken);
        await context.Response.WriteAsync($"event: done\ndata: {done}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
