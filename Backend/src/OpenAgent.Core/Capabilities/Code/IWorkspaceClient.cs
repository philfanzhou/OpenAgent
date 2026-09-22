using OpenAgent.Contracts.Execution;

namespace OpenAgent.Core.Capabilities.Code;

/// <summary>
/// 会话工作区的 Runner 客户端：与 <see cref="ICodeExecutor"/> 同一 Runner 服务，
/// 按（sessionKey, 相对路径）读写宿主侧工作区（bind-mount 进会话沙箱 /work）。
/// </summary>
internal interface IWorkspaceClient
{
    Task<WorkspaceListResult> ListAsync(
        string sessionKey, string? path, string? pattern, CancellationToken cancellationToken);

    Task<WorkspaceReadResult> ReadAsync(
        string sessionKey, string path, int offsetLine, int limitLines, CancellationToken cancellationToken);

    Task<WorkspaceWriteResult> WriteAsync(
        string sessionKey, string path, string content, CancellationToken cancellationToken);

    Task<WorkspaceEditResult> EditAsync(
        string sessionKey, string path, string oldString, string newString, bool replaceAll,
        CancellationToken cancellationToken);

    Task<WorkspaceBytesResult> ReadBytesAsync(
        string sessionKey, string path, CancellationToken cancellationToken);

    Task<WorkspaceWriteResult> UploadAsync(
        string sessionKey, string path, byte[] content, CancellationToken cancellationToken);
}

/// <summary>Runner 工作区端点返回的领域错误（HTTP 状态 + ProblemDetails detail）。</summary>
internal sealed class WorkspaceOperationException(int statusCode, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
}
