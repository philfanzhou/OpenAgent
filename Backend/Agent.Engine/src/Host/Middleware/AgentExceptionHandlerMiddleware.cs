using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Host.Middleware;

/// <summary>
/// Unified exception-handling middleware for the Engine pipeline. SSE endpoints
/// (path ending with /sse) receive an error/done event stream frame so the SSE
/// semantics are preserved; all other endpoints receive an RFC 7807 ProblemDetails
/// payload mapped from the exception.
/// </summary>
internal class AgentExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AgentExceptionHandlerMiddleware> _logger;
    private readonly ErrorMapper _errorMapper;

    public AgentExceptionHandlerMiddleware(
        RequestDelegate next,
        ILogger<AgentExceptionHandlerMiddleware> logger,
        ErrorMapper errorMapper)
    {
        _next = next;
        _logger = logger;
        _errorMapper = errorMapper;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            if (IsSseEndpoint(context))
            {
                await HandleSseErrorAsync(context, ex);
            }
            else
            {
                await HandleExceptionAsync(context, ex);
            }
        }
    }

    /// <summary>
    /// Uses EndsWith for an exact match, avoiding false positives like /sse-search.
    /// </summary>
    private static bool IsSseEndpoint(HttpContext context)
    {
        return context.Request.Path.Value?.EndsWith("/sse", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task HandleSseErrorAsync(HttpContext context, Exception exception)
    {
        var traceId = TraceIdResolver.Resolve(context);
        if (exception is not AgentException)
        {
            EngineLog.SseEndpointErrorOccurred(
                _logger,
                exception,
                context.Request.Method,
                context.Request.Path.ToString(),
                traceId,
                context.Response.HasStarted);
        }

        if (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
        }

        var errorEvent = StreamingPayloadFactory.CreateErrorPayload(exception, traceId);

        await context.Response.WriteAsync("event: error\n", CancellationToken.None);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(errorEvent)}\n\n", CancellationToken.None);
        await context.Response.WriteAsync("event: done\n", CancellationToken.None);
        await context.Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
        await context.Response.Body.FlushAsync(CancellationToken.None);
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var traceId = TraceIdResolver.Resolve(context);

        if (context.Response.HasStarted)
        {
            EngineLog.UnhandledExceptionAfterResponseStart(_logger, exception, context.Request.Method, context.Request.Path.ToString(), traceId);
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        var (statusCode, problemDetails) = _errorMapper.Map(exception, traceId, context.Request.Path);

        if (exception is not AgentException)
        {
            EngineLog.UnhandledExceptionMappedToProblemDetails(
                _logger,
                exception,
                context.Request.Method,
                context.Request.Path.ToString(),
                statusCode,
                traceId);
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problemDetails, jsonOptions));
    }
}
