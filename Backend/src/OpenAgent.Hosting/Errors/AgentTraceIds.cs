using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace OpenAgent.Hosting.Errors;

/// <summary>
/// 统一的 TraceId 解析顺序：X-Trace-Id 请求头 → Activity.Current.Id → HttpContext.TraceIdentifier。
/// 所有产生错误载荷的代码（中间件、端点）都应经此处解析，保证同一请求内 traceId 一致。
/// </summary>
public static class AgentTraceIds
{
    public static string Resolve(HttpContext context) =>
        context.Request.Headers["X-Trace-Id"].FirstOrDefault()
        ?? Activity.Current?.Id
        ?? context.TraceIdentifier;
}
