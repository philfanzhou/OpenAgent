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
    /// 默认 0（关闭）：当前 OpenCode 版本空闲进程存在持续 CPU/网络活动（实测 1.19.3 每进程
    /// 约 1-2 核空转 + 持续外呼），确认目标版本空闲行为可接受后再按会话开启。
    /// </summary>
    public int KeepAliveSeconds { get; set; } = 0;

    /// <summary>保活进程总数上限；超出时直接销毁而不是归还池。</summary>
    public int MaxKeptProcesses { get; set; } = 8;

    /// <summary>单个进程最多服务的 ACP 会话数；超过即销毁重建，约束 harness 侧会话/内存累积。</summary>
    public int MaxSessionsPerProcess { get; set; } = 64;
}
