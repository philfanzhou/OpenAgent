using System.Text.Json;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal static class StreamingForwardingError
{
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        string? action,
        ForwarderError error,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (!IsStreamingAction(action))
        {
            return CreateJsonFallback(error, traceId);
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

    private static IResult CreateJsonFallback(ForwarderError error, string traceId)
    {
        if (error == ForwarderError.RequestTimedOut)
        {
            return Results.Json(new
            {
                Status = "GatewayTimeout",
                Message = "The request to the AI engine timed out. Please try again later.",
                Fallback = true,
                TraceId = traceId
            }, statusCode: StatusCodes.Status504GatewayTimeout);
        }

        return Results.Json(new
        {
            Status = "ServiceUnavailable",
            Message = "The AI engine is temporarily unavailable. A fallback response is provided.",
            Fallback = true,
            TraceId = traceId
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
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
            error = $"Forwarding failed: {error}",
            traceId
        });

        await context.Response.WriteAsync($"event: error\ndata: {payload}\n\n", cancellationToken);
        await context.Response.WriteAsync("event: done\ndata: [ERROR]\n\n", cancellationToken);
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
        });
        var doneLine = JsonSerializer.Serialize(new
        {
            type = "done",
            status = "error",
            traceId
        });

        await context.Response.WriteAsync(errorLine + "\n", cancellationToken);
        await context.Response.WriteAsync(doneLine + "\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
