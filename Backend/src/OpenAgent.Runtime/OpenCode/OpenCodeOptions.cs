namespace OpenAgent.Runtime.OpenCode;

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

    /// <summary>诊断开关：把每轮 usage_update（上下文占用 used/size）写入日志。默认关闭。</summary>
    public bool LogUsageUpdates { get; set; }

    /// <summary>
    /// 空闲进程保活秒数：相同会话归属与启动身份的进程在窗口内复用，追问轮次免付冷启动。
    /// 默认保留 300 秒，最多保留 16 个空闲会话；设为 0 可关闭并在每轮结束时回收全部状态。
    /// </summary>
    public int KeepAliveSeconds { get; set; } = 300;

    /// <summary>空闲会话上限；池满时淘汰最久未使用的会话。</summary>
    public int MaxKeptProcesses { get; set; } = 16;

    /// <summary>单个进程最多服务的 ACP 会话数；超过即销毁重建，约束 harness 侧会话/内存累积。</summary>
    public int MaxSessionsPerProcess { get; set; } = 64;

    /// <summary>
    /// 额外放行的 OpenCode 原生工具名（如 read/edit/write，可带参数模式如 bash[npm *]）。
    /// 默认空：原生工具保持 "*": "deny"，仅平台桥 openagent_* 与透传 MCP/skill 可用。
    /// 放行即把对应主机读写/执行面直接交给 harness 进程（应用层授权，非 OS 沙箱），
    /// 由宿主配置自担；条目参与进程复用身份，改动后不会复用旧权限进程。
    /// </summary>
    public string[] AllowedNativeTools { get; set; } = [];
}
