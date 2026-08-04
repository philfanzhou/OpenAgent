
## Feature


## 核心能力

Agent.Core 以 **只读引用** 方式访问 Agent.Matrix 的配置与权限数据，通过 `IAgentConfigProvider` 获取 Agent 运行时所需的全部配置。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `IAgentConfigProvider` | `Agent.Contracts/Configuration/IAgentConfigProvider.cs` | Agent 配置提供者接口 |
| `AgentConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | Agent 运行时配置模型 |
| `AgentSummary` | `Agent.Contracts/Configuration/IAgentConfigProvider.cs` | Agent 摘要信息 |
| `LlmConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | LLM 配置 |
| `McpConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | MCP 配置 |
| `RagConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | RAG 配置 |
| `SkillsConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | 技能配置 |

## 数据流向

```
Agent.Matrix → IAgentConfigProvider → AgentConfig
                                        ├── FrameworkType    → 决定引擎类型
                                        ├── Llm             → LLM 连接配置
                                        ├── Mcp             → MCP 服务器列表
                                        ├── Rag             → RAG 实例配置
                                        ├── Skills          → 技能列表
                                        └── MaxTurns        → 最大工具调用轮次
```

- Agent.Core 通过 `IAgentConfigProvider` 获取配置
- Agent.Core **绝不** 向 Matrix 写入任何数据

## IAgentConfigProvider 核心方法

```csharp
Task<AgentConfig> GetConfigAsync(CancellationToken ct = default);
Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken ct = default);
Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken ct = default);
```

## Specification


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

## Design


`AgentRun` 在一次 run 前固定配置快照：

```text
AgentRun
  -> IdentityResolution
       -> AgentIdResolver
       -> ExecutionConfigResolver -> IAgentConfigProvider
       -> AgentAuthorizationGate
       -> ILlmRegistry
  -> MafAgentFactory -> MafChatClientFactory -> ChatClientAgent
```

| 配置 | 消费者 |
|---|---|
| `Llm` | `IdentityResolution` 固定授权快照；`MafChatClientFactory` 创建 `IChatClient` |
| `Llm.Temperature` | `MafAgentFactory` 构建 MAF `ChatOptions` |
| `Mcp.Servers` | `ToolAssembler` |
| `Rag` | `ToolAssembler` / `MafCapabilityProvider` |
| `Skills` | `ISkillProvider` |
| `MaxTurns` | `FunctionInvokingChatClient.MaximumIterationsPerRequest` |

`FrameworkType` 只用于旧配置反序列化兼容，不再选择生产引擎。Core 不关心
`IAgentConfigProvider` 的数据来源，可以是 Matrix、Redis、本地配置或测试内存实现。

## Tasks


## 已完成

- [x] `IAgentConfigProvider` 接口定义（含 `GetConfigAsync` 和 `ListAgentsAsync`）
- [x] `AgentConfig` 完整数据模型（FrameworkType/Llm/Mcp/Rag/Skills/MaxTurns）
- [x] `AgentSummary` 摜要模型
- [x] `LlmConfig` / `McpConfig` / `RagConfig` / `SkillsConfig` 子配置模型
- [x] `McpServerConfig` 支持 SSE/Stdio 两种类型
- [x] `RagInstanceConfig` 支持 ACL 权限控制（AllowedUserIds/Groups/TenantIds/Roles）
- [x] `SkillInstanceConfig` 支持 ACL 权限控制
- [x] `Service` 中 AgentId 多级解析
- [x] `FrameworkType` 多级回退（Config → Context/Header → Mock）

## 待办

- [ ] 补充 `IAgentConfigProvider` 各实现类的文档
- [ ] 补充配置变更通知机制文档

## Tests


## 单元测试

### IAgentConfigProvider

- `GetConfigAsync()` 无参调用返回默认配置
- `GetConfigAsync(agentId)` 按 AgentId 返回对应配置
- `GetConfigAsync(agentId)` AgentId 不存在时返回 null
- `ListAgentsAsync()` 返回所有可用 Agent 摘要

### AgentConfig 模型

- `FrameworkType` 默认值为 `MAF`
- `MaxTurns` 默认值为 50
- `Llm.Temperature` 默认值为 0.7
- `Llm.ModelId` 默认值为 "gpt-4o"
- `Rag.Enabled` 默认值为 false

### Service 中的配置使用

- AgentId 解析优先级（context → Header → Items → default）
- FrameworkType 解析优先级（Config → Context/Header → Mock）
- 配置不存在时抛出 `InvalidOperationException`
- `MaxTurns` 为 0 时使用默认值 5

## 集成测试

- 配置获取端到端流程
- 多 Agent 配置隔离

## Conventions


## 只读原则

- Agent.Core **绝不** 向 Matrix 写入数据
- 所有配置变更由 Matrix 侧发起
- Agent.Core 仅消费配置，不生产配置

## AgentId 解析约定

`AgentIdResolver` 由主机中间件统一解析，优先级：

- 优先从调用上下文 `context["AgentId"]` 获取
- 其次从 `IAgentRequestContext.AgentId` 获取（主机 `AgentRequestContextMiddleware` 统一填充）
- 再次从 HTTP Header `X-Agent-Id` 获取
- 再次从 `HttpContext.Items["AgentId"]` 获取
- 最终回退到 `"default"`

## FrameworkType 解析约定

- 优先使用 `AgentConfig.FrameworkType`
- 配置未设置时通过 `FrameworkTypeResolver` 从 Context/Header 解析
- 最终回退到 `EngineFrameworkType.Mock`

## 配置模型约定

- `AgentConfig` 中所有子配置均有默认实例（`new()`），不为 null
- `LlmConfig.Provider` 为空字符串时，`LlmRegistry.ResolveConfig` 原样返回
- `McpConfig.Servers` 支持数组格式（对象）和简写字符串格式（URL）
- `RagConfig` 可通过 `EnabledRagInstanceIds` 或 `Instances` 两种方式配置 RAG 实例
- `MaxTurns` 默认 50，`Service` 中实际默认使用 5（当 config.MaxTurns <= 0 时）

## 禁止事项

- ❌ 不调用 Matrix 的写入 API
- ❌ 不修改 Redis 中 Matrix 管理的 Key
- ❌ 不依赖 Matrix 的内部实现细节
- ❌ 不缓存过期的 AgentConfig（由 Provider 实现决定缓存策略）
