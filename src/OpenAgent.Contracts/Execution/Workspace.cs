namespace OpenAgent.Contracts.Execution;

/// <summary>
/// 会话工作区（Runner 宿主侧 session-&lt;key&gt;/work 目录，bind-mount 到沙箱 /work）
/// 的文件操作契约，由 Engine 的工具层与 Runner 的 /api/v1/workspace/* 端点共享。
/// 所有 path 均为相对于工作区根的 POSIX 风格相对路径（拒绝 rooted/../ 与符号链接逃逸）。
/// </summary>
public sealed class WorkspaceListRequest
{
    public string? Path { get; set; }
    /// <summary>可选通配符（* 与 ?），匹配当前层文件名。</summary>
    public string? Pattern { get; set; }
}

public sealed class WorkspaceListResult
{
    public string Path { get; set; } = string.Empty;
    public IReadOnlyList<WorkspaceEntry> Entries { get; set; } = [];
}

public sealed class WorkspaceEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long LengthBytes { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
}

public sealed class WorkspaceReadRequest
{
    public string Path { get; set; } = string.Empty;
    /// <summary>起始行号（1 起，默认 1）。</summary>
    public int OffsetLine { get; set; } = 1;
    /// <summary>行数（1..2000，默认 500）。</summary>
    public int LimitLines { get; set; } = 500;
}

public sealed class WorkspaceReadResult
{
    public string Path { get; set; } = string.Empty;
    public int TotalLines { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    /// <summary>cat -n 风格行号文本（"行号\t内容"）。</summary>
    public string Content { get; set; } = string.Empty;
    public bool Truncated { get; set; }
}

public sealed class WorkspaceWriteRequest
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed class WorkspaceWriteResult
{
    public string Path { get; set; } = string.Empty;
    public long LengthBytes { get; set; }
}

public sealed class WorkspaceEditRequest
{
    public string Path { get; set; } = string.Empty;
    public string OldString { get; set; } = string.Empty;
    public string NewString { get; set; } = string.Empty;
    public bool ReplaceAll { get; set; }
}

public sealed class WorkspaceEditResult
{
    public string Path { get; set; } = string.Empty;
    public int Replacements { get; set; }
}

public sealed class WorkspaceBytesRequest
{
    public string Path { get; set; } = string.Empty;
}

public sealed class WorkspaceBytesResult
{
    public string Path { get; set; } = string.Empty;
    public long LengthBytes { get; set; }
    public string ContentBase64 { get; set; } = string.Empty;
}

public sealed class WorkspaceUploadRequest
{
    public string Path { get; set; } = string.Empty;
    public string ContentBase64 { get; set; } = string.Empty;
}

/// <summary>Runner 工作区端点可映射为 HTTP 状态码的领域错误。</summary>
public enum WorkspaceErrorKind
{
    InvalidPath = 400,
    NotFound = 404,
    EditConflict = 409,
    TooLarge = 413
}
