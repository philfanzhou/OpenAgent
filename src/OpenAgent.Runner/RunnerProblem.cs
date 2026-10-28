using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace OpenAgent.Runner;

/// <summary>
/// Runner 本地的 ProblemDetails 构造器。Runner 刻意只依赖 Contracts（沙箱 sidecar 保持轻依赖），
/// 因此以最小实现复刻 OpenAgent.Hosting.Errors 的统一错误契约：
/// type/title/status/detail + 扩展字段 traceId/timestamp/code。
/// </summary>
internal static class RunnerProblem
{
    internal static ProblemDetails Create(
        string type,
        string title,
        int status,
        string detail,
        HttpContext context)
    {
        var problem = new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        problem.Extensions["timestamp"] = DateTimeOffset.UtcNow;
        problem.Extensions["code"] = type.Substring(type.LastIndexOf('/') + 1);
        return problem;
    }

    internal static string Serialize(ProblemDetails problem) => JsonSerializer.Serialize(
        problem,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
