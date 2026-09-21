using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Hosting.Errors;

/// <summary>
/// 所有 HTTP 服务共享的全局异常处理中间件：把未处理异常映射为 RFC 7807 ProblemDetails
/// （含 traceId/timestamp/errorCode/code 扩展字段）。流式（SSE）端点在响应已开始后
/// 改发 error/done 事件。领域异常（<see cref="AgentException"/>）按 Debug 记录以控制噪音，
/// 其余按 Error 记录。
/// </summary>
public sealed class AgentExceptionHandlerMiddleware
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<AgentExceptionHandlerMiddleware> _logger;
    private readonly AgentExceptionMapper _errorMapper;
    private readonly AgentExceptionHandlingOptions _options;

    public AgentExceptionHandlerMiddleware(
        RequestDelegate next,
        ILogger<AgentExceptionHandlerMiddleware> logger,
        AgentExceptionMapper errorMapper,
        IOptions<AgentExceptionHandlingOptions> options)
    {
        _next = next;
        _logger = logger;
        _errorMapper = errorMapper;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            if (_options.IsStreamingRequest(context))
            {
                await HandleStreamingErrorAsync(context, exception).ConfigureAwait(false);
            }
            else
            {
                await HandleExceptionAsync(context, exception).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleStreamingErrorAsync(HttpContext context, Exception exception)
    {
        string traceId = AgentTraceIds.Resolve(context);
        LogMappedException(context, exception, traceId, statusCode: null);

        if (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        if (!context.Response.HasStarted)
        {
            await WriteProblemDetailsAsync(context, exception, traceId).ConfigureAwait(false);
            return;
        }

        var (_, problemDetails) = _errorMapper.Map(
            exception, traceId, context.Request.Path, IncludeExceptionDetails(context));
        StreamingErrorPayload payload = _options.CreateStreamingErrorPayload?.Invoke(exception, traceId, problemDetails)
            ?? new StreamingErrorPayload
            {
                Type = problemDetails.Type ?? string.Empty,
                Title = problemDetails.Title ?? string.Empty,
                Detail = problemDetails.Detail ?? string.Empty,
                TraceId = traceId
            };

        // camelCase 序列化错误载荷：前端 parseSseBlock 读取 detail/title/traceId（小写）。
        string error = JsonSerializer.Serialize(payload, SseJsonOptions);
        string done = JsonSerializer.Serialize(new { done = true, status = "error" }, SseJsonOptions);
        await context.Response.WriteAsync($"event: error\ndata: {error}\n\n", CancellationToken.None).ConfigureAwait(false);
        await context.Response.WriteAsync($"event: done\ndata: {done}\n\n", CancellationToken.None).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        string traceId = AgentTraceIds.Resolve(context);

        if (context.Response.HasStarted)
        {
            _logger.LogError(
                exception,
                "Unhandled exception after response started. Method: {Method}, Path: {Path}, TraceId: {TraceId}",
                context.Request.Method, context.Request.Path, traceId);
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        await WriteProblemDetailsAsync(context, exception, traceId).ConfigureAwait(false);
    }

    private async Task WriteProblemDetailsAsync(HttpContext context, Exception exception, string traceId)
    {
        var (statusCode, problemDetails) = _errorMapper.Map(
            exception,
            traceId,
            context.Request.Path,
            IncludeExceptionDetails(context));
        LogMappedException(context, exception, traceId, statusCode);

        await AgentProblemDetailsWriter.WriteAsync(
            context, problemDetails, CancellationToken.None).ConfigureAwait(false);
    }

    private void LogMappedException(HttpContext context, Exception exception, string traceId, int? statusCode)
    {
        if (exception is AgentException)
        {
            _logger.LogDebug(
                "Agent exception mapped to ProblemDetails. Method: {Method}, Path: {Path}, Status: {StatusCode}, TraceId: {TraceId}",
                context.Request.Method, context.Request.Path, statusCode, traceId);
            return;
        }

        _logger.LogError(
            exception,
            "Unhandled exception mapped to ProblemDetails. Method: {Method}, Path: {Path}, Status: {StatusCode}, TraceId: {TraceId}",
            context.Request.Method, context.Request.Path, statusCode, traceId);
    }

    private static bool IncludeExceptionDetails(HttpContext context) =>
        context.RequestServices?.GetService<IHostEnvironment>()?.IsDevelopment() == true;
}
