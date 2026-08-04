# Agent.Matrix — 规格说明

## 接口契约

### IAgentConfigProvider

```csharp
// Agent.Contracts/Configuration/IAgentConfigProvider.cs
public interface IAgentConfigProvider
{
    Task<AgentConfig> GetConfigAsync(CancellationToken ct = default);
    Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken ct = default);
}
```

### AgentConfig

```csharp
// Agent.Contracts/Configuration/AgentConfig.cs
public class AgentConfig
{
    public EngineFrameworkType FrameworkType { get; set; }  // 引擎类型（默认 MAF）
    public LlmConfig Llm { get; set; }                      // LLM 配置
    public McpConfig Mcp { get; set; }                      // MCP 配置
    public RagConfig Rag { get; set; }                      // RAG 配置
    public SkillsConfig Skills { get; set; }                // 技能配置
    public int MaxTurns { get; set; }                       // 最大工具调用轮次（默认 50）
}
```

### AgentSummary

```csharp
// Agent.Contracts/Configuration/IAgentConfigProvider.cs
public sealed class AgentSummary
{
    public string AgentId { get; init; }          // Agent 唯一标识
    public string Name { get; init; }             // 显示名称
    public int Status { get; init; }              // 状态
    public string CurrentVersion { get; init; }   // 当前版本
    public string Framework { get; init; }        // 框架类型
}
```

### 子配置模型

#### LlmConfig

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| Provider | string | "" | 关联的 LlmProviderProfile.Id |
| Format | ApiFormat | OpenAICompatible | API 格式 |
| ModelId | string | "gpt-4o" | 模型标识 |
| ApiKey | string | "" | API 密钥 |
| Endpoint | string | "" | API 端点 |
| Temperature | double | 0.7 | 温度参数 |

#### McpConfig

| 字段 | 类型 | 说明 |
|------|------|------|
| Servers | List\<McpServerConfig\> | MCP 服务器列表 |

#### McpServerConfig

| 字段 | 类型 | 说明 |
|------|------|------|
| Name | string | 服务器名称 |
| Url | string | 服务器 URL |
| Type | McpServerType | 连接类型（SSE/Stdio） |

#### RagConfig

| 字段 | 类型 | 说明 |
|------|------|------|
| Enabled | bool | 是否启用 RAG |
| EnabledRagInstanceIds | List\<string\> | 启用的 RAG 实例 ID 列表 |
| Instances | List\<RagInstanceConfig\> | RAG 实例配置列表 |

#### SkillsConfig

| 字段 | 类型 | 说明 |
|------|------|------|
| EnabledSkills | List\<string\> | 启用的技能 ID 列表 |
| Instances | List\<SkillInstanceConfig\> | 技能实例配置列表 |
