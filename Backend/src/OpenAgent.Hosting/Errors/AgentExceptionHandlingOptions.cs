using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Hosting.Errors;

/// <summary>SSE 流式端点的错误事件载荷（event: error 的 data，camelCase 序列化）。</summary>
public sealed record StreamingErrorPayload
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required string TraceId { get; init; }
}

/// <summary>
/// 全局异常处理的共享配置。Engine.Host 用它注入 SDK 异常映射与中文 SSE 文案；
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
    public List<Func<Exception, string, AgentMappedError?>> ExtraExceptionMappers { get; } = [];

    public static bool DefaultIsStreaming(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        return path.EndsWith("/chat/stream", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/chat/sse", StringComparison.OrdinalIgnoreCase);
    }
}
