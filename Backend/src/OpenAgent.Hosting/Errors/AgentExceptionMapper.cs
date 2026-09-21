using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Hosting.Errors;

/// <summary>一次异常映射的结果：HTTP 状态码与 ProblemDetails 载荷。</summary>
public sealed record AgentMappedError(int StatusCode, ProblemDetails ProblemDetails);

/// <summary>
/// 异常到 HTTP 状态码 + ProblemDetails 的核心映射，Engine.Host / Router 共用。
/// 宿主可通过 <see cref="AgentExceptionHandlingOptions.ExtraExceptionMappers"/> 注册服务特定异常
/// （例如 Engine 的 OpenAI/Azure SDK ClientResultException），它们先于核心映射执行。
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
        foreach (Func<Exception, string, AgentMappedError?> extra in _options.ExtraExceptionMappers)
        {
            AgentMappedError? mapped = extra(exception, traceId);
            if (mapped != null)
            {
                return (mapped.StatusCode, mapped.ProblemDetails);
            }
        }

        return exception switch
        {
            UnauthorizedAccessException => (403, AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/unauthorized", "Unauthorized", 403,
                "Access denied due to insufficient permissions", exception.Message, traceId)),
            HumanApprovalRequiredException approval => (202, AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/approval-required", "HumanApprovalRequired", 202,
                "Action requires human approval", approval.Message, traceId,
                approval.ErrorCode,
                ("approvalToken", approval.ApprovalToken ?? string.Empty),
                ("actionDescription", approval.ActionDescription))),
            AgentException agent => (MapAgentErrorCode(agent.ErrorCode), AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/{agent.ErrorCode.ToSymbolicName()}",
                agent.ErrorCode.ToString(), MapAgentErrorCode(agent.ErrorCode), agent.Message,
                agent.Details ?? agent.Message, traceId, agent.ErrorCode)),
            TimeoutException => (504, AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/timeout", "GatewayTimeout", 504,
                "The request timed out", exception.Message, traceId)),
            HttpRequestException httpException => MapHttpRequestException(httpException, instance, traceId),
            _ => (500, AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/internal-error", "InternalServerError", 500,
                string.IsNullOrWhiteSpace(exception.Message) ? "An unexpected error occurred" : exception.Message,
                includeExceptionDetails ? exception.ToString() : "Please contact support if the problem persists",
                traceId))
        };
    }

    private static (int StatusCode, ProblemDetails ProblemDetails) MapHttpRequestException(
        HttpRequestException exception,
        string? instance,
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
            AgentErrorCode.DependencyUnavailable));
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
