namespace OpenAgent.Contracts.Requests;

public enum AgentErrorCode : int
{
    Success = 0,

    PermissionDenied = 100,

    UnauthorizedSkill = 1001,
    SkillNotFound = 1002,
    SkillExecutionFailed = 1003,
    SkillTimeout = 1004,
    SkillValidationFailed = 1005,
    SkillQuotaExceeded = 1006,

    McpConnectionFailed = 2001,
    McpToolNotFound = 2002,
    McpToolExecutionFailed = 2003,
    McpConnectionTimeout = 2004,
    McpServerUnavailable = 2005,

    RagRetrievalFailed = 3001,
    RagIndexNotFound = 3002,
    RagPermissionDenied = 3003,

    LlmProviderNotSupported = 4001,
    LlmConnectionFailed = 4002,
    LlmTimeout = 4003,
    LlmQuotaExceeded = 4004,
    LlmInvalidResponse = 4005,
    LlmModelNotFound = 4006,

    TenantMismatch = 5001,
    TenantNotFound = 5002,
    TenantDataIsolationViolation = 5003,

    AudiencePermissionDenied = 6001,
    AudienceMismatch = 6002,

    HumanApprovalRequired = 7001,
    HumanApprovalDenied = 7002,
    HumanApprovalTimeout = 7003,

    InvalidRequest = 8001,
    MissingRequiredField = 8002,
    InvalidIdempotencyKey = 8003,

    // 通用 HTTP 语义错误码：端点直接构造错误响应（资源不存在、未认证、限流）时使用，
    // 与上面的请求校验错误码共用 8xxx 段，不与领域错误码（1xxx-7xxx）混用。
    NotFound = 8004,
    AuthenticationRequired = 8005,
    RateLimited = 8006,

    Conflict = 8101,

    InternalError = 9001,
    PipelineExecutionFailed = 9002,
    ConfigurationError = 9003,
    DependencyUnavailable = 9004,
}
