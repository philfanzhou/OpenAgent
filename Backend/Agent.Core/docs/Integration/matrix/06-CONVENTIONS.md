# Agent.Matrix — 约定

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
