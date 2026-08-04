using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Engine.Host.Middleware;

internal sealed class ErrorMapper(ProblemDetailsFactory problemDetailsFactory)
{
    internal (int StatusCode, ProblemDetails ProblemDetails) Map(
        Exception exception,
        string traceId,
        string instance)
    {
        return exception switch
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
            _ => (500, problemDetailsFactory.Create(
                "https://error.agent.com/internal-error", "InternalServerError", 500,
                "An unexpected error occurred", "Please contact support if the problem persists", traceId))
        };
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
