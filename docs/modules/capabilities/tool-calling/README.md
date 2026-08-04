
## Feature


## 用户故事

作为执行内核，我希望不同来源的能力（Skill、MCP、RAG）在工具调用层面保持一致的语义，以便模型看到统一的工具集合并可靠地执行工具调用。

## 概述

工具调用体系是 Agent.Core 中模型与外部能力交互的统一机制。不同来源的能力在工具调用层面保持一致的语义，由 Service 根据工具名称判断来源并路由执行。

## 工具来源

| 来源 | 标识规则 | 说明 |
|------|---------|------|
| Core 内部工具 | 原始名称 | 通过 IToolRegistry 注册 |
| 本地 Skill | 原始名称 | 实现 ISkill 接口 |
| MCP 外部工具 | `mcp_{server}_{tool}` 前缀 | 通过 IMcpClient 发现 |
| RAG 检索 | `search_knowledge_base` 固定名称 | 通过 RagSearchTool 实现 |

## 核心能力

- 统一工具集合：模型看到的是 ToolDefinition 列表，无需感知来源差异
- 原生 Function Calling：引擎返回 ToolCall → ExecuteToolAsync → 继续推理
- XML 降级：TryExtractToolUse 解析 `<tool_use>` XML 标签
- 工具路由：search_knowledge_base → RAG, mcp_* → MCP, 其余 → Skill
- 最大轮次控制：默认 5（可配置）

## 当前状态

**已实现** — 原生 Function Calling 和 XML 降级均已落地，工具路由完整。

## 当前限制

- XML 降级模式下工具结果以 user 角色消息追加（非标准 tool 角色）
- 无并行工具调用执行（逐个串行执行 ToolCalls）
- 无工具调用结果缓存

## Architecture


`MafCapabilityProvider` 是工具能力进入 Agent 的唯一运行时入口。

```text
MAF AIContextProvider
  -> ToolAssembler: Skill / RAG / MCP discovery authorization
  -> MafToolAdapter: CapabilityFunction -> AIFunction
  -> FunctionInvokingChatClient: native tool loop
  -> ToolCallDispatcher: execute authorization + audit
  -> Skill / RAG / MCP
```

平台不实现模型/工具迭代，不合并流式 ToolCall，也不解析 XML 降级协议。
`FunctionInvokingChatClient` 负责函数调用、结果回填、循环终止和最大迭代次数。

发现阶段执行 Skill、MCP、Tool、Function 的可见性授权；`AIFunction` 执行体再次执行
execute 授权，避免发现与调用之间的权限变化。MCP runtime name 只用于避免同名冲突，
授权和审计仍使用原始 server/tool 身份。

工具名称、描述和 JSON Schema 只存在于 `AITool` 元数据，不再重复写入 system prompt。

## Data Models


| 类型 | 所属 | 用途 |
|---|---|---|
| `CapabilityFunction` | Core internal | 授权发现后的名称、描述和 JSON Schema |
| `AIFunction` / `AITool` | Microsoft.Extensions.AI | 模型可见的原生工具与执行体 |
| `FunctionCallContent` | Microsoft.Extensions.AI | MAF 生成的函数调用 |
| `FunctionResultContent` | Microsoft.Extensions.AI | MAF 回填的函数结果 |
| `McpToolIdentity` | Core internal | runtime name 与原始 server/tool 身份绑定 |

Core 不定义 Engine message/request/result DTO。消息和工具循环全部使用 MAF 与
Microsoft.Extensions.AI 类型。

## API


## 发现

`ToolAssembler` 从 Skill、MCP 和 RAG 收集 `ToolDefinition`，执行 discover authorization，
并为同名 MCP 工具生成稳定且唯一的 runtime function name。

## MAF 函数

`MafToolAdapter` 把每个发现结果转换为 `AIFunction`：

- 名称、描述和 JSON Schema 来自发现快照；
- `AIFunction` 保存原始 `ToolDefinition`；
- MAF 的 `FunctionInvokingChatClient` 负责调用和结果回填；
- 执行体进入 `ToolCallDispatcher`，再次执行资源授权并调用具体能力。

平台不提供 `IAgentEngine` 或 `IAgentService` 工具回调 API，也不自己执行模型工具循环。

## Registry

`IToolRegistry` 仍用于平台运行时工具注册：

| 方法 | 说明 |
|---|---|
| `RegisterTool` | 注册 definition 和 executor |
| `GetTools` | 返回发现定义 |
| `ExecuteToolAsync` | 执行已注册工具 |
| `HasTool` | 检查名称 |

## 调用链

```text
AgentRun
  -> ChatClientAgent
  -> FunctionInvokingChatClient
  -> AIFunction
  -> ToolCallDispatcher
  -> Skill / MCP / RAG
```

## Tests


## 测试策略

工具调用的测试围绕工具收集、执行路由、调用循环三个核心链路展开。

## 单元测试

### 工具收集

| 测试场景 | 验证点 |
|----------|--------|
| Skill 描述符转为 ToolDefinition | Name/Description/Schema 正确传递 |
| RAG 工具添加 | config.Rag.Enabled=true 且 RagSearchTool 存在时添加 |
| RAG 工具不添加 | config.Rag.Enabled=false 时不添加 |
| MCP 工具加载 | 别名生成正确，绑定注册正确 |
| MCP 服务器加载失败 | Warning 日志，跳过该服务器 |

### 执行路由

| 测试场景 | 醴证点 |
|----------|--------|
| search_knowledge_base | 路由到 RagSearchTool |
| mcp_ 前缀工具 | 路由到 ExecuteMcpToolAsync |
| 普通工具 | 路由到 SkillProvider.ExecuteAsync |
| agent_tools- 前缀 | 去除前缀后路由 |
| AgentException 直接抛出 | 不包装 |
| 其他异常 | 返回 "Error executing tool: ..." 字符串 |

### 参数解析

| 测试场景 | 验证点 |
|----------|--------|
| 空字符串 | 返回空 Dictionary |
| 有效 JSON | 反序列化为 Dictionary |
| 无效 JSON | 原始字符串作为 query 键值 |

### MCP 别名生成

| 测试场景 | 验证点 |
|----------|--------|
| 正常名称 | mcp_{server}_{tool} 格式 |
| 特殊字符归一化 | 非字母数字替换为下划线，转小写 |
| 重名处理 | 追加 _2, _3 后缀 |

### XML 降级

| 测试场景 | 验证点 |
|----------|--------|
| 包含 `<tool_use>` 标签 | 正确提取 name 和 arguments |
| 不包含 `<tool_use>` 标签 | 返回 false |
| 缺少 name/arguments 标签 | 返回空字符串 |

## 集成测试

| 测试场景 | 验证点 |
|----------|--------|
| 完整工具调用循环 | 模型返回 ToolCall → 执行 → 结果回填 → 继续推理 |
| 多轮工具调用 | turn 计数正确，达到 maxTurns 时终止 |
| 流式工具调用 | chunk 合并正确，工具结果正确回填 |
| 取消时写回 | OperationCanceledException → 状态 Cancelled |
| 失败时写回 | 异常 → 状态 Failed |

## 验收口径

- [ ] 三种工具来源均能正确收集为 ToolDefinition
- [ ] 执行路由按名称前缀正确分发
- [ ] 原生 Function Calling 和 XML 降级均能工作
- [ ] maxTurns 限制生效
- [ ] 工具失败不破坏执行链状态

## Conventions


## 命名约定

- MCP 工具使用 `mcp_{server}_{tool}` 前缀，避免与本地 Skill 混淆
- RAG 工具使用固定系统名称 `search_knowledge_base`，避免和业务工具冲突
- 业务 Skill 名称应体现业务动作，而不是实现细节
- 以 `agent_tools-` 开头的工具名在执行前会自动去除前缀

## 执行路径约定

- 模型看到的是统一 ToolDefinition 集合
- Service 根据工具名称前缀判断来源并路由
- 不同来源的能力在主流程中保持一致的调用语义
- 工具结果以字符串形式回填到消息历史

## 参数约定

- 参数结构由 ParametersJsonSchema 定义
- 参数名稳定，不随实现变化
- 参数语义与工具描述一致
- 错误参数可以被识别和记录
- 无效 JSON 参数降级为 `{ "query": rawString }`

## 结果约定

- 工具结果为字符串类型
- 成功结果可直接用于后续对话推理
- 失败结果以 `"Error"` 前缀标识
- 原生 Function Calling：工具结果作为 `tool` 角色消息写回
- XML 降级：工具结果作为 `user` 角色消息写回

## 会话约定

- 工具调用前后 assistant 和 tool 消息关系必须清楚
- ToolCallId 用于关联请求与响应
- 工具调用消息纳入会话持久化

## 错误处理约定

- 错误可以被日志和追踪定位
- 不把底层实现细节直接暴露给最终用户
- AgentException 直接传播，其他异常包装为错误字符串
- 工具失败不会破坏整条执行链的状态一致性

## 循环控制约定

- 最大工具调用轮次由 AgentConfig.MaxTurns 控制，默认 5
- 达到上限时返回最后一条 assistant 消息或 "Max turns reached."
