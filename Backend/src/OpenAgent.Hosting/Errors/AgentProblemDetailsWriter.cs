using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace OpenAgent.Hosting.Errors;

/// <summary>
/// 把 ProblemDetails 以统一形态（camelCase、忽略 null、application/problem+json）写入响应，
/// 供不便返回 IResult 的中间件（限流、认证上下文等）使用，保证与端点/全局异常处理输出一致。
/// </summary>
public static class AgentProblemDetailsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync(
        HttpContext context,
        ProblemDetails problemDetails,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problemDetails, JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }
}
