using OpenAgent.Contracts.Execution;

namespace OpenAgent.Core.Integrations.Runner;

/// <summary>Runner 工作区端点返回的领域错误（HTTP 状态 + ProblemDetails detail）。</summary>
internal sealed class WorkspaceOperationException(int statusCode, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
}
