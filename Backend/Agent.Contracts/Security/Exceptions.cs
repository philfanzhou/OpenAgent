using OpenAgent.Contracts.Requests;

namespace OpenAgent.Contracts.Security;

public class AgentException : Exception
{
    public AgentErrorCode ErrorCode { get; }
    public string? Details { get; }

    public AgentException(AgentErrorCode errorCode, string? message = null, string? details = null, Exception? innerException = null)
        : base(message ?? errorCode.ToString(), innerException)
    {
        ErrorCode = errorCode;
        Details = details;
    }
}

public class ToolExecutionException : AgentException
{
    public string ToolName { get; }
    public Dictionary<string, object>? Arguments { get; }

    public ToolExecutionException(string toolName, string? message = null, Dictionary<string, object>? arguments = null, Exception? innerException = null)
        : base(AgentErrorCode.SkillExecutionFailed, message ?? $"Tool '{toolName}' execution failed", details: toolName, innerException: innerException)
    {
        ToolName = toolName;
        Arguments = arguments;
    }
}

public class HumanApprovalRequiredException : AgentException
{
    public string ActionDescription { get; }
    public string? ApprovalToken { get; }

    public HumanApprovalRequiredException(string actionDescription, string? approvalToken = null, string? message = null)
        : base(AgentErrorCode.HumanApprovalRequired, message ?? $"Human approval required for: {actionDescription}", details: approvalToken)
    {
        ActionDescription = actionDescription;
        ApprovalToken = approvalToken;
    }
}

public class AudiencePermissionDeniedException : AgentException
{
    public IReadOnlyList<string> DeniedAudiences { get; }
    public string? RequiredPermission { get; }

    public AudiencePermissionDeniedException(IReadOnlyList<string> deniedAudiences, string? requiredPermission = null, string? message = null)
        : base(AgentErrorCode.AudiencePermissionDenied, message ?? "One or more audience members lack permission", details: requiredPermission)
    {
        DeniedAudiences = deniedAudiences;
        RequiredPermission = requiredPermission;
    }
}

public class TenantDataIsolationException : AgentException
{
    public string? TenantId { get; }
    public string? RequestedTenantId { get; }

    public TenantDataIsolationException(string? tenantId = null, string? requestedTenantId = null, string? message = null)
        : base(AgentErrorCode.TenantDataIsolationViolation, message ?? "Tenant data isolation violation detected", details: $"{tenantId} vs {requestedTenantId}")
    {
        TenantId = tenantId;
        RequestedTenantId = requestedTenantId;
    }
}
