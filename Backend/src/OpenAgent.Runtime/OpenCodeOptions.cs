namespace OpenAgent.Runtime;

/// <summary>仅由宿主配置的 OpenCode 启动参数，聊天请求不能指定可执行文件或工作目录。</summary>
public sealed class OpenCodeOptions
{
    public const string SectionName = "OpenCode";
    /// <summary>命令名或绝对路径。默认 "opencode"，按 PATH、npm 安装布局解析，Windows 最后经 cmd shim 兜底。</summary>
    public string Executable { get; set; } = "opencode";
    public string[] Arguments { get; set; } = [];
    public string WorkspaceRoot { get; set; } = Path.Combine(Path.GetTempPath(), "openagent-acp");
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int TurnTimeoutSeconds { get; set; } = 1800;
    public int MaxConcurrentTurns { get; set; } = 8;
    public int MaxPromptCharacters { get; set; } = 2 * 1024 * 1024;
}
