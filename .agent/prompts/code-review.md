# 代码审查检查清单

对 OpenAgent 仓库的代码变更进行分层审查。

---

## 第 1 层：Agent.Contracts（接口层）

审查重点：变更不会破坏下游消费者。

- [ ] 公共接口签名是否向后兼容？
- [ ] 新增接口是否真的属于 Contracts 层？（无实现依赖）
- [ ] DTO 的 JSON 序列化是否考虑了 camelCase 命名策略？
- [ ] 错误码是否使用 `AgentErrorCodes` 统一定义？
- [ ] 是否引入了对其他项目的引用？（Contracts 是叶子节点）

---

## 第 2 层：Agent.Core（核心逻辑层）

审查重点：引擎、管道、能力模块的正确性。

- [ ] Pipeline 中间件注册顺序是否正确？（AgentIdValidation → TenantValidation → Tracing → Auth → AuditLogging）
- [ ] 新 Engine 是否正确实现了 `IAgentEngine`（ChatCompletion + StreamChatCompletion）？
- [ ] 异步方法是否正确传播 CancellationToken？
- [ ] `IAsyncEnumerable<T>` 流式返回是否正确释放资源？
- [ ] DI 注册：中间件用 `AddScoped`，注册表用 `AddSingleton`？
- [ ] 错误是否通过中间件管道传播（不吞掉异常）？

---

## 第 3 层：Agent.Engine / Agent.Router（宿主层）

审查重点：服务配置、端点、中间件的正确性。

- [ ] appsettings.json 中的服务连接配置是否正确？
- [ ] 新增端点是否遵循 `/api/v1/agent/` 前缀约定？
- [ ] Dockerfile 是否需要更新？
- [ ] 路由/网关的限流和熔断配置是否合理？

---

## 第 4 层：TestCode（测试层）

审查重点：测试覆盖率和测试质量。

- [ ] 新增功能是否有对应的测试？
  - Core 逻辑 → xunit 单元测试
  - 跨服务行为 → MSTest 集成测试
  - 端到端流程 → PowerShell E2E 脚本
- [ ] 测试命名是否清晰？（方法名_场景_预期行为）
- [ ] Mock 是否正确模拟了外部依赖？
- [ ] `InternalsVisibleTo` 是否正确配置了测试项目？

---

## 通用检查

- [ ] 新增 NuGet 包是否有明确理由？（见 `.agent/rules/coding-conventions.md`）
- [ ] 是否违反了项目依赖方向？（Contracts ← Core ← Engine/Router ← Host）
- [ ] `async void` 是否被避免？
- [ ] sync/async 是否混用在同一条调用链中？
- [ ] `var` 是否只在类型明显时使用？
- [ ] Nullable 引用类型是否正确处理了 null 检查？
- [ ] 新功能的文档是否需要更新？（Agent.Core/docs/、AGENTS.md、.agent/）

---

## 安全检查（安全相关变更时必查）

- [ ] 认证/授权是否通过中间件统一处理（不跳过管道）？
- [ ] API Key / Token 是否被硬编码？（必须用环境变量或 .env）
- [ ] SQL 查询是否使用参数化（防止注入）？
- [ ] 敏感数据是否被意外记录到日志中？

---

## 按变更类型聚焦

| 变更类型 | 重点检查 |
|---------|---------|
| 新增 Engine | Contracts 接口、Core 实现、DI 注册 |
| 新增 MCP 工具 | MCP 服务实现、Agent 配置、集成测试 |
| 新增 Skill | Skill 基类遵循、SkillRegistry 注册、MCP 客户端调用 |
| 新增 LLM 提供商 | OpenAI 兼容性、API Key 格式、E2E 测试 |
| 修改 Pipeline | 中间件顺序、CancellationToken 传播 |
| 基础设施变更 | .csproj、Dockerfile、CI（如存在） |
