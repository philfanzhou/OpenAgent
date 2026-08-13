using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using System.ClientModel;
using System.Net;
using System.Net.Http;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Engine.Host.Middleware;

internal sealed class ErrorMapper(ProblemDetailsFactory problemDetailsFactory)
{
    internal (int StatusCode, ProblemDetails ProblemDetails) Map(
        Exception exception,
        string traceId,
        string instance)
        => Map(exception, traceId, instance, includeExceptionDetails: false);

    internal (int StatusCode, ProblemDetails ProblemDetails) Map(
        Exception exception,
        string traceId,
        string instance,
        bool includeExceptionDetails) => exception switch
        {
            UnauthorizedAccessException => (403, problemDetailsFactory.Create(
                "https://error.agent.com/unauthorized", "Unauthorized", 403,
                "Access denied due to insufficient permissions", exception.Message, traceId)),
            HumanApprovalRequiredException approval => (202, problemDetailsFactory.Create(
                "https://error.agent.com/approval-required", "HumanApprovalRequired", 202,
                "Action requires human approval", approval.Message, traceId,
                ("approvalToken", approval.ApprovalToken ?? string.Empty),
                ("actionDescription", approval.ActionDescription))),
            AgentException agent => (MapAgentErrorCode(agent.ErrorCode), problemDetailsFactory.Create(
                $"https://error.agent.com/{agent.ErrorCode.ToString().ToLowerInvariant()}",
                agent.ErrorCode.ToString(), MapAgentErrorCode(agent.ErrorCode), agent.Message,
                agent.Details ?? agent.Message, traceId, ("errorCode", (int)agent.ErrorCode))),
            TimeoutException => (504, problemDetailsFactory.Create(
                "https://error.agent.com/timeout", "GatewayTimeout", 504,
                "The request timed out", exception.Message, traceId)),
            ClientResultException clientException => MapProviderException(
                clientException,
                traceId),
            HttpRequestException httpException => MapHttpRequestException(
                httpException,
                traceId,
                instance),
            _ => (500, problemDetailsFactory.Create(
                "https://error.agent.com/internal-error", "InternalServerError", 500,
                string.IsNullOrWhiteSpace(exception.Message) ? "An unexpected error occurred" : exception.Message,
                includeExceptionDetails ? exception.ToString() : "Please contact support if the problem persists",
                traceId))
        };

    private (int StatusCode, ProblemDetails ProblemDetails) MapProviderException(
        ClientResultException exception,
        string traceId)
    {
        int statusCode = exception.Status is >= 400 and < 500 ? exception.Status : 502;
        return (statusCode, problemDetailsFactory.Create(
            "https://error.agent.com/provider-request-error",
            "ProviderRequestFailed",
            statusCode,
            StreamingPayloadFactory.FormatProviderError(exception.Status, exception.Message),
            exception.Message,
            traceId,
            ("errorCode", (int)AgentErrorCode.DependencyUnavailable)));
    }

    private (int StatusCode, ProblemDetails ProblemDetails) MapHttpRequestException(
        HttpRequestException exception,
        string traceId,
        string instance)
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
        return (statusCode, problemDetailsFactory.Create(
            $"https://error.agent.com/{title.ToLowerInvariant()}",
            title,
            statusCode,
            "The model provider request failed.",
            exception.Message,
            traceId,
            ("errorCode", (int)AgentErrorCode.DependencyUnavailable)));
    }

    internal static int MapAgentErrorCode(AgentErrorCode errorCode) => errorCode switch
    {
        AgentErrorCode.PermissionDenied or AgentErrorCode.UnauthorizedSkill
            or AgentErrorCode.AudiencePermissionDenied or AgentErrorCode.RagPermissionDenied
            or AgentErrorCode.HumanApprovalDenied => 403,
        AgentErrorCode.SkillNotFound or AgentErrorCode.McpToolNotFound
            or AgentErrorCode.RagIndexNotFound or AgentErrorCode.LlmModelNotFound => 404,
        AgentErrorCode.SkillQuotaExceeded or AgentErrorCode.LlmQuotaExceeded => 429,
        AgentErrorCode.InvalidRequest or AgentErrorCode.MissingRequiredField
            or AgentErrorCode.InvalidIdempotencyKey or AgentErrorCode.SkillValidationFailed => 400,
        AgentErrorCode.TenantMismatch or AgentErrorCode.TenantNotFound
            or AgentErrorCode.TenantDataIsolationViolation => 400,
        AgentErrorCode.Conflict => 409,
        AgentErrorCode.DependencyUnavailable => 503,
        _ => 500
    };
}
