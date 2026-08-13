using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Host.Middleware;

/// <summary>
/// Unified exception-handling middleware for the Engine pipeline. The SSE stream
/// endpoint receives an error/done event sequence; all other endpoints receive
/// an RFC 7807 ProblemDetails payload mapped from the exception.
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

    private static bool IsSseEndpoint(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        return path.EndsWith("/chat/stream", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleSseErrorAsync(HttpContext context, Exception exception)
    {
        string traceId = TraceIdResolver.Resolve(context);
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
            await WriteProblemDetailsAsync(context, exception, traceId).ConfigureAwait(false);
            return;
        }

        // 用 camelCase 序列化错误载荷：前端 parseSseBlock 读取 detail/title/traceId（小写）。
        // 否则 PascalCase 的 Detail 与前端字段不匹配，前端只会显示兜底的“Agent 执行失败”。
        string error = JsonSerializer.Serialize(
            StreamingPayloadFactory.CreateErrorPayload(exception, traceId),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        string done = JsonSerializer.Serialize(new { done = true, status = "error" });
        await context.Response.WriteAsync($"event: error\ndata: {error}\n\n", CancellationToken.None).ConfigureAwait(false);
        await context.Response.WriteAsync($"event: done\ndata: {done}\n\n", CancellationToken.None).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteProblemDetailsAsync(
        HttpContext context,
        Exception exception,
        string traceId)
    {
        var (statusCode, problemDetails) = _errorMapper.Map(
            exception,
            traceId,
            context.Request.Path,
            includeExceptionDetails: IsDevelopment(context));
        EngineLog.UnhandledExceptionMappedToProblemDetails(
            _logger,
            exception,
            context.Request.Method,
            context.Request.Path.ToString(),
            statusCode,
            traceId);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problemDetails, jsonOptions),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var traceId = TraceIdResolver.Resolve(context);

        if (context.Response.HasStarted)
        {
            EngineLog.UnhandledExceptionAfterResponseStart(_logger, exception, context.Request.Method, context.Request.Path.ToString(), traceId);
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        var (statusCode, problemDetails) = _errorMapper.Map(
            exception,
            traceId,
            context.Request.Path,
            includeExceptionDetails: IsDevelopment(context));

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

    private static bool IsDevelopment(HttpContext context) =>
        context.RequestServices?.GetService<IHostEnvironment>()?.IsDevelopment() == true;
}
