using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Hosting.Errors;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// 转发失败时的降级响应：非流式请求返回统一 ProblemDetails；流式请求在响应已开始后
/// 发 error/done 事件（SSE 载荷与 Engine 的 {type,title,detail,traceId} 形态一致，
/// NDJSON 保持 {type,error:{title,detail,traceId}} 结构）。
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
            if (string.Equals(action, "stream", StringComparison.OrdinalIgnoreCase))
            {
                await WriteNdjsonErrorAsync(context, error, traceId, cancellationToken);
            }
            else
            {
                await WriteSseErrorAsync(context, error, traceId, cancellationToken);
            }
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
                AgentErrorCode.DependencyUnavailable));
        }

        return TypedResults.Problem(AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/service-unavailable",
            "ServiceUnavailable",
            StatusCodes.Status503ServiceUnavailable,
            "The AI engine is temporarily unavailable.",
            context.Request.Path,
            traceId,
            AgentErrorCode.DependencyUnavailable));
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

    private static async Task WriteNdjsonErrorAsync(
        HttpContext context,
        ForwarderError error,
        string traceId,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-cache";

        var errorLine = JsonSerializer.Serialize(new
        {
            type = "error",
            error = new
            {
                title = error == ForwarderError.RequestTimedOut ? "GatewayTimeout" : "ServiceUnavailable",
                detail = $"Forwarding failed: {error}",
                traceId
            },
            traceId
        }, JsonOptions);
        var doneLine = JsonSerializer.Serialize(new
        {
            type = "done",
            status = "error",
            traceId
        }, JsonOptions);

        await context.Response.WriteAsync(errorLine + "\n", cancellationToken);
        await context.Response.WriteAsync(doneLine + "\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
