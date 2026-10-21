namespace OpenAgent.Contracts.Configuration;

/// <summary>租户独立保存的运行时配置；模型、Skill 和 MCP 继续按原有入口绑定。</summary>
public sealed class RuntimeProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = "OpenCode";
    public int TurnTimeoutSeconds { get; set; } = 1800;
}
