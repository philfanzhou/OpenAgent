using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Hosting.Errors;

/// <summary>
/// 统一的 ProblemDetails（RFC 7807）构造器，所有 HTTP 服务共享同一错误载荷形态：
/// 标准字段 type/title/status/detail/instance，加上扩展字段 traceId、timestamp、
/// errorCode（整数，<see cref="AgentErrorCode"/>）与 code（可读符号名）。
/// 端点内直接返回 TypedResults.Problem(AgentProblemDetails.Invalid(...)) 即可。
/// </summary>
public static class AgentProblemDetails
{
    public const string TypePrefix = "https://error.agent.com";

    public static ProblemDetails Create(
        string type,
        string title,
        int status,
        string detail,
        string? instance = null,
        string? traceId = null,
        AgentErrorCode? errorCode = null,
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
        if (errorCode != null)
        {
            problemDetails.Extensions["errorCode"] = (int)errorCode.Value;
            problemDetails.Extensions["code"] = errorCode.Value.ToSymbolicName();
        }
        foreach (var (key, value) in extensions)
        {
            problemDetails.Extensions[key] = value;
        }

        return problemDetails;
    }

    /// <summary>400 请求无效（参数缺失、格式错误等）。</summary>
    public static ProblemDetails Invalid(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/invalid-request", "InvalidRequest", 400, detail,
        context?.Request.Path, Resolve(context), AgentErrorCode.InvalidRequest);

    /// <summary>404 资源不存在。</summary>
    public static ProblemDetails NotFound(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/not-found", "NotFound", 404, detail,
        context?.Request.Path, Resolve(context), AgentErrorCode.NotFound);

    /// <summary>401 未认证（缺少或无效的凭证）。</summary>
    public static ProblemDetails AuthenticationRequired(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/authentication-required", "AuthenticationRequired", 401, detail,
        context?.Request.Path, Resolve(context), AgentErrorCode.AuthenticationRequired);

    /// <summary>403 已认证但无权访问。</summary>
    public static ProblemDetails PermissionDenied(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/permission-denied", "PermissionDenied", 403, detail,
        context?.Request.Path, Resolve(context), AgentErrorCode.PermissionDenied);

    /// <summary>409 冲突（并发版本不匹配等）。</summary>
    public static ProblemDetails Conflict(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/conflict", "Conflict", 409, detail,
        context?.Request.Path, Resolve(context), AgentErrorCode.Conflict);

    /// <summary>429 请求过多（限流）。</summary>
    public static ProblemDetails RateLimited(string detail, HttpContext? context = null) => Create(
        $"{TypePrefix}/rate-limited", "RateLimited", 429, detail,
        context?.Request.Path, Resolve(context), AgentErrorCode.RateLimited);

    /// <summary>把领域异常映射为 ProblemDetails（状态码由 <see cref="AgentExceptionMapper.MapAgentErrorCode"/> 决定）。</summary>
    public static ProblemDetails From(AgentException exception, HttpContext? context = null)
    {
        int status = AgentExceptionMapper.MapAgentErrorCode(exception.ErrorCode);
        return exception switch
        {
            HumanApprovalRequiredException approval => Create(
                $"{TypePrefix}/approval-required", "HumanApprovalRequired", 202,
                "Action requires human approval",
                context?.Request.Path, Resolve(context), approval.ErrorCode,
                ("approvalToken", approval.ApprovalToken ?? string.Empty),
                ("actionDescription", approval.ActionDescription)),
            _ => Create(
                $"{TypePrefix}/{exception.ErrorCode.ToSymbolicName()}",
                exception.ErrorCode.ToString(), status, exception.Message,
                context?.Request.Path, Resolve(context), exception.ErrorCode)
        };
    }

    private static string? Resolve(HttpContext? context) =>
        context == null ? null : AgentTraceIds.Resolve(context);
}
