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
