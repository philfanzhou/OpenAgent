using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Hosting.Errors;

/// <summary>
/// 统一的 ProblemDetails（RFC 7807）构造器，所有 HTTP 服务共享同一错误载荷形态：
/// 标准字段 type/title/status/detail/instance，加上扩展字段 traceId、timestamp、
/// errorCode（整数，<see cref="AgentErrorCode"/>）。端点内直接返回
/// <c>TypedResults.Problem(AgentProblemDetails.Invalid(...))</c> 即可。
/// </summary>
public static class AgentProblemDetails
{
    public const string TypePrefix = "https://error.agent.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ProblemDetails Create(
        string type,
        string title,
        int status,
        string detail,
        string? instance = null,
        string? traceId = null,
        params (string Key, object Value)[] extensions)
    {
        var problemDetails = new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = detail,
            Instance = instance
        };
        if (traceId != null)
        {
            problemDetails.Extensions["traceId"] = traceId;
        }
        problemDetails.Extensions["timestamp"] = DateTimeOffset.UtcNow;
        foreach (var (key, value) in extensions)
        {
            problemDetails.Extensions[key] = value;
        }

        return problemDetails;
    }

    /// <summary>400 请求无效（参数缺失、格式错误等）。</summary>
    public static ProblemDetails Invalid(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/invalid-request", "InvalidRequest", 400, detail,
        context?.Request.Path, TraceId(context), ("errorCode", (int)AgentErrorCode.InvalidRequest));

    /// <summary>404 资源不存在。</summary>
    public static ProblemDetails NotFound(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/not-found", "NotFound", 404, detail,
        context?.Request.Path, TraceId(context), ("errorCode", (int)AgentErrorCode.NotFound));

    /// <summary>401 未认证（缺少或无效的凭证）。</summary>
    public static ProblemDetails AuthenticationRequired(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/authentication-required", "AuthenticationRequired", 401, detail,
        context?.Request.Path, TraceId(context), ("errorCode", (int)AgentErrorCode.AuthenticationRequired));

    /// <summary>403 已认证但无权访问。</summary>
    public static ProblemDetails PermissionDenied(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/permission-denied", "PermissionDenied", 403, detail,
        context?.Request.Path, TraceId(context), ("errorCode", (int)AgentErrorCode.PermissionDenied));

    /// <summary>409 冲突（并发版本不匹配等）。</summary>
    public static ProblemDetails Conflict(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/conflict", "Conflict", 409, detail,
        context?.Request.Path, TraceId(context), ("errorCode", (int)AgentErrorCode.Conflict));

    /// <summary>429 请求过多（限流）。</summary>
    public static ProblemDetails RateLimited(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/rate-limited", "RateLimited", 429, detail,
        context?.Request.Path, TraceId(context), ("errorCode", (int)AgentErrorCode.RateLimited));

    /// <summary>
    /// 把 ProblemDetails 以统一形态（camelCase、忽略 null、application/problem+json）写入响应，
    /// 供不便返回 IResult 的中间件（限流、转发失败等）使用。
    /// </summary>
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

    /// <summary>
    /// 统一的 TraceId 解析顺序：X-Trace-Id 请求头 → Activity.Current.Id → HttpContext.TraceIdentifier。
    /// 所有产生错误载荷的代码都经此处解析，保证同一请求内 traceId 一致。
    /// </summary>
    public static string ResolveTraceId(HttpContext context) =>
        context.Request.Headers["X-Trace-Id"].FirstOrDefault()
        ?? Activity.Current?.Id
        ?? context.TraceIdentifier;

    private static string? TraceId(HttpContext? context) =>
        context is null ? null : ResolveTraceId(context);

    /// <summary>枚举名转 kebab-case 符号名（TenantDataIsolationViolation → tenant-data-isolation-violation），用于 type URI。</summary>
    internal static string ToSymbolicName(AgentErrorCode errorCode)
    {
        StringBuilder builder = new();
        foreach (char character in errorCode.ToString())
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('-');
            }
            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
