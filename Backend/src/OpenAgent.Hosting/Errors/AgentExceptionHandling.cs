using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Hosting.Errors
{

/// <summary>SSE 流式端点的错误事件载荷（event: error 的 data，camelCase 序列化）。</summary>
public sealed record StreamingErrorPayload
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required string TraceId { get; init; }
}

/// <summary>一次异常映射的结果：HTTP 状态码与 ProblemDetails 载荷。</summary>
public sealed record AgentMappedError(int StatusCode, ProblemDetails ProblemDetails);

/// <summary>
/// 全局异常处理的共享配置。Engine 用它注入 SDK 异常映射与中文 SSE 文案；
/// Router / Runner 使用默认行为。
/// </summary>
public sealed class AgentExceptionHandlingOptions
{
    /// <summary>
    /// 判定请求是否为流式（SSE）端点：流式端点在响应已开始后改用 error/done 事件返回错误，
    /// 响应未开始时仍返回 ProblemDetails。默认匹配 /chat/stream 与 /chat/sse 结尾的路径。
    /// </summary>
    public Func<HttpContext, bool> IsStreamingRequest { get; set; } = DefaultIsStreaming;

    /// <summary>
    /// 自定义 SSE 错误载荷（例如 Engine 的中文友好文案）。为 null 时从映射出的
    /// ProblemDetails 投影出 {type,title,detail,traceId}。
    /// </summary>
    public Func<Exception, string, ProblemDetails, StreamingErrorPayload>? CreateStreamingErrorPayload { get; set; }

    /// <summary>服务特定异常映射，先于核心映射执行；返回 null 表示交回核心映射。</summary>
    public Func<Exception, string, AgentMappedError?>? ExtraExceptionMapper { get; set; }

    public static bool DefaultIsStreaming(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        return path.EndsWith("/chat/stream", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/chat/sse", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 异常到 HTTP 状态码 + ProblemDetails 的核心映射，Engine.Host / Router 共用。
/// 宿主通过 <see cref="AgentExceptionHandlingOptions.ExtraExceptionMapper"/> 注册服务特定异常
/// （例如 Engine 的 OpenAI/Azure SDK ClientResultException）。
/// </summary>
public sealed class AgentExceptionMapper
{
    private readonly AgentExceptionHandlingOptions _options;

    public AgentExceptionMapper(IOptions<AgentExceptionHandlingOptions> options)
    {
        _options = options.Value;
    }

    public (int StatusCode, ProblemDetails ProblemDetails) Map(
        Exception exception,
        string traceId,
        string? instance = null,
        bool includeExceptionDetails = false)
    {
        if (_options.ExtraExceptionMapper?.Invoke(exception, traceId) is { } mapped)
        {
            return (mapped.StatusCode, mapped.ProblemDetails);
        }

        return exception switch
        {
            UnauthorizedAccessException => (403, AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/unauthorized", "Unauthorized", 403,
                "Access denied due to insufficient permissions", exception.Message, traceId)),
            HumanApprovalRequiredException approval => (202, AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/approval-required", "HumanApprovalRequired", 202,
                "Action requires human approval", approval.Message, traceId,
                ("approvalToken", approval.ApprovalToken ?? string.Empty),
                ("actionDescription", approval.ActionDescription))),
            AgentException agent => (MapAgentErrorCode(agent.ErrorCode), AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/{AgentProblemDetails.ToSymbolicName(agent.ErrorCode)}",
                agent.ErrorCode.ToString(), MapAgentErrorCode(agent.ErrorCode), agent.Message,
                agent.Details ?? agent.Message, traceId, ("errorCode", (int)agent.ErrorCode))),
            TimeoutException => (504, AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/timeout", "GatewayTimeout", 504,
                "The request timed out", exception.Message, traceId)),
            HttpRequestException httpException => MapHttpRequestException(httpException, traceId),
            _ => (500, AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/internal-error", "InternalServerError", 500,
                string.IsNullOrWhiteSpace(exception.Message) ? "An unexpected error occurred" : exception.Message,
                includeExceptionDetails ? exception.ToString() : "Please contact support if the problem persists",
                traceId))
        };
    }

    private static (int StatusCode, ProblemDetails ProblemDetails) MapHttpRequestException(
        HttpRequestException exception,
        string traceId)
    {
        int statusCode = exception.StatusCode switch
        {
            HttpStatusCode.Unauthorized => 401,
            HttpStatusCode.Forbidden => 403,
            HttpStatusCode.NotFound => 404,
            HttpStatusCode.TooManyRequests => 429,
            >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => (int)exception.StatusCode.Value,
            _ => 503
        };
        string title = statusCode switch
        {
            401 => "ProviderUnauthorized",
            403 => "ProviderForbidden",
            404 => "ProviderNotFound",
            429 => "ProviderRateLimited",
            _ => "DependencyUnavailable"
        };
        return (statusCode, AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/{title.ToLowerInvariant()}",
            title,
            statusCode,
            "The model provider request failed.",
            exception.Message,
            traceId,
            ("errorCode", (int)AgentErrorCode.DependencyUnavailable)));
    }

    public static int MapAgentErrorCode(AgentErrorCode errorCode) => errorCode switch
    {
        AgentErrorCode.PermissionDenied or AgentErrorCode.UnauthorizedSkill
            or AgentErrorCode.AudiencePermissionDenied or AgentErrorCode.RagPermissionDenied
            or AgentErrorCode.HumanApprovalDenied => 403,
        AgentErrorCode.SkillNotFound or AgentErrorCode.McpToolNotFound
            or AgentErrorCode.RagIndexNotFound or AgentErrorCode.LlmModelNotFound
            or AgentErrorCode.NotFound => 404,
        AgentErrorCode.SkillQuotaExceeded or AgentErrorCode.LlmQuotaExceeded
            or AgentErrorCode.RateLimited => 429,
        AgentErrorCode.InvalidRequest or AgentErrorCode.MissingRequiredField
            or AgentErrorCode.InvalidIdempotencyKey or AgentErrorCode.SkillValidationFailed => 400,
        AgentErrorCode.AuthenticationRequired => 401,
        AgentErrorCode.TenantMismatch => 403,
        AgentErrorCode.TenantNotFound or AgentErrorCode.TenantDataIsolationViolation => 400,
        AgentErrorCode.Conflict => 409,
        AgentErrorCode.DependencyUnavailable => 503,
        _ => 500
    };
}

/// <summary>
/// 所有 HTTP 服务共享的全局异常处理中间件：把未处理异常映射为 RFC 7807 ProblemDetails
/// （含 traceId/timestamp/errorCode 扩展字段）。流式（SSE）端点在响应已开始后
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
        string traceId = AgentProblemDetails.ResolveTraceId(context);
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
        string traceId = AgentProblemDetails.ResolveTraceId(context);

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

        await AgentProblemDetails.WriteAsync(
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

}

namespace OpenAgent.Hosting
{
    public static class AgentErrorServiceCollectionExtensions
    {
        /// <summary>
        /// 注册共享的异常映射与全局异常处理中间件设施。configure 可注入服务特定行为
        /// （额外异常映射、SSE 错误文案、流式端点判定）。
        /// </summary>
        public static IServiceCollection AddAgentErrorHandling(
            this IServiceCollection services,
            Action<Errors.AgentExceptionHandlingOptions>? configure = null)
        {
            if (configure != null)
            {
                services.Configure(configure);
            }
            services.TryAddSingleton<Errors.AgentExceptionMapper>();
            return services;
        }

        /// <summary>把共享的全局异常处理中间件加入管道；应放在认证之后、端点映射之前。</summary>
        public static IApplicationBuilder UseAgentErrorHandling(this IApplicationBuilder app) =>
            app.UseMiddleware<Errors.AgentExceptionHandlerMiddleware>();
    }
}