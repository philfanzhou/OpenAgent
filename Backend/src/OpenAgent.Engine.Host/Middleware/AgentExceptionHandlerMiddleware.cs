using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Host.Middleware;

/// <summary>
/// Unified exception-handling middleware for the Engine pipeline. All endpoints
/// receive an RFC 7807 ProblemDetails payload mapped from the exception.
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
            await HandleExceptionAsync(context, ex);
        }
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
