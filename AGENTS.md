# AGENTS.md

## 仓库概览

OpenAgent — 基于 .NET 8.0 的多服务 Agent 平台（C#, ASP.NET Core）。

- `Backend/` — 生产代码：Contracts → Core → Engine，Hosting 提供共享宿主能力
- `TestCode/` — 测试服务、集成测试、PowerShell 脚本

## 构建与测试

构建与测试命令见 `.agent/skills/build-and-test.md`

## E2E 测试环境

- 服务与端口 → `.agent/skills/service-lifecycle.md`
- E2E 工作流 → `.agent/skills/e2e-test.md`
- API Key 与提供商 → `.agent/skills/add-llm-provider.md`

> 完整的 E2E 详情、API 端点、Agent 配置 JSON 结构、数据流、Redis 键、排查指南 → [e2e-test-guide.md](TestCode/docs/e2e-test-guide.md)

## 架构

```
Backend/
├── Agent.Contracts/       共享接口、模型、DTO
├── Agent.Core/            核心逻辑（管道、中间件、引擎适配、会话锁）
├── Agent.Engine/          Engine 服务（Redis 注册、健康检查、热更新）+ Host（ASP.NET Core）
└── Agent.Hosting/         共享 DI、认证、Redis 与 OpenTelemetry 注册扩展
```

`Agent.Workflow` 当前只有规划文档，没有纳入仓库或部署拓扑；不要把它当作可调用服务。
状态与兼容配置说明见 `Backend/Agent.Workflow.md`。

### 分布式部署的会话一致性

多 Engine 实例部署时，会话一致性由两层保证：

| 层级 | 机制 | 职责 |
|------|------|------|
| **Core**（硬保证） | 分布式锁 `lock:conversation:{tenantId}:{conversationId}`（Redis SET NX EX + owner token + 心跳，详见 `Agent.Core/docs/modules/execution/conversation-lock.md`） | 同一会话串行执行；推理始终基于最新上下文 |

客户端发送：**JWT**（认证）+ **conversationId**（会话跟踪）+ **query**（用户输入）。

## 关键约定

- `InternalsVisibleTo` 用于测试访问 — 在假设 `internal` 可见性前先检查 `.csproj`
- TestMCP 使用 SQLite，数据目录通过 `OPENAGENT_TEST_DATA_DIR` 环境变量指定
- Engine Dockerfile：`Agent.Engine/src/Host/Dockerfile`；Linux 脚本：`Agent.Engine/script/`
- 编码规则：`.agent/rules/coding-conventions.md`（依赖方向、.NET 版本、NuGet 策略、命名、可见性、DI、异步、错误处理、合规检查清单）

## AI 资源

`.agent/` 目录结构与约定 → `.agent/README.md`

## 任务路由

| 任务 | 参考 |
|------|------|
| 添加 LLM 提供商 | `.agent/skills/add-llm-provider.md`, `Agent.Core/docs/Integration/llm-provider/` |
| 添加 MCP 工具/服务 | `.agent/skills/add-mcp-tool.md`, `Agent.Core/docs/modules/capabilities/mcp/` |
| 添加 Agent 技能 | `.agent/skills/add-agent-skill.md`, `Agent.Core/docs/modules/capabilities/skill/` |
| 创建 Agent 配置 | `.agent/skills/create-agent-config.md`, `TestCode/docs/e2e-test-guide.md` §5 |
| 添加新引擎 | `.agent/skills/add-engine.md`, `Agent.Core/src/<EngineName>/` |
| 修改 Channels / Playground | `.agent/skills/channels-development.md` |
| 编写测试 | `.agent/prompts/write-tests.md` |
| 集成调试 | `.agent/prompts/debug-integration.md`, `TestCode/docs/mcp-test-guide.md` |
| Trace/Log/Metrics 排查 | `.agent/skills/trace-troubleshoot.md` |
| 规划新功能 | `.agent/prompts/new-feature-planning.md` |
| 代码审查 | `.agent/prompts/code-review.md` |
| 构建与单元测试 | `.agent/skills/build-and-test.md` |
| 验证变更（自动） | `.agent/skills/verify-changes.md` — 自主验证策略 |
| 运行 E2E 测试 | `.agent/skills/e2e-test.md`, `TestCode/docs/e2e-test-guide.md` |
| 启动/停止服务 | `.agent/skills/service-lifecycle.md` |
| 技能对比测试 | `TestCode/docs/skill-demo-test-guide.md` |
| 整理/修复/添加文档 | `.agent/skills/update-docs.md` → `.agent/prompts/doc-standards.md` |

## 文档索引

- `.agent/rules/coding-conventions.md` — 权威 .NET 编码规范
- `Agent.Core/docs/overview/` — SystemContext、Design、Requirements、KeyFlows、Integration、DataOwnership
- `Agent.Core/docs/modules/` — capabilities（MCP、RAG、skill、tool-calling）、engine、security、execution
- `Agent.Core/docs/Integration/` — LLM provider、MCP server、RAG service、Redis、SQL Server
- `TestCode/docs/e2e-test-guide.md` — 完整 E2E：架构、API、配置 JSON、数据流、Redis 键、排查
- `TestCode/docs/mcp-test-guide.md` — MCP + Skill 集成测试
- `TestCode/docs/skill-demo-test-guide.md` — 技能对比与演示验证
- `.agent/prompts/doc-standards.md` — 文档规范：结构、模板、命名、质量检查清单
