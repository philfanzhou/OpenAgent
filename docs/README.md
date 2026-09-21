# OpenAgent 文档中心

> 源码目录不散落文档，所有正式文档统一位于本目录。风格与政策见 `.agent/rules/doc-standards.md`。

## overview/ — 全局认知

- [SystemContext.md](./overview/SystemContext.md) — 服务定位、上下游、参与者
- [Design.md](./overview/Design.md) — 分层、项目职责、请求主链、技术栈
- [KeyFlows.md](./overview/KeyFlows.md) — 会话/文件/Engine 协调/Router 路由关键流程
- [DataOwnership.md](./overview/DataOwnership.md) — 数据主责、引用边界、双写禁区
- [Requirements.md](./overview/Requirements.md) — 平台级需求摘要
- [Observability.md](./overview/Observability.md) — 日志/Trace/Metrics/健康检查的当前事实
- [Integration.md](./overview/Integration.md) — 集成矩阵与失败语义

## modules/ — 一域一文件

- [execution.md](./modules/execution.md) — 执行管线、流式输出、错误处理、会话锁
- [conversation.md](./modules/conversation.md) — 会话持久化、交互日志、上下文压缩
- [capabilities.md](./modules/capabilities.md) — 工具调用、Skill、代码执行、MCP、RAG
- [security.md](./modules/security.md) — 认证、租户校验、权限
- [engine.md](./modules/engine.md) — MAF 运行时、配置管理/热更新、健康检查、停机、服务注册、Chat API、Host 错误处理
- [router.md](./modules/router.md) — 服务发现、限流降级、下游就绪
- [chat-workspace.md](./modules/chat-workspace.md) — Gateway-first Chat 工作台

## integrations/ — 外部依赖集成

- [llm-provider.md](./integrations/llm-provider.md) — 大模型推理（OpenAI/Azure/Anthropic）
- [agent-provider.md](./integrations/agent-provider.md) — Router 意图识别与第三方 Agent 服务接入
- [file-assets.md](./integrations/file-assets.md) — 用户文件、S3 兼容对象存储、预览与模型文件能力
- [code-runner.md](./integrations/code-runner.md) — Bubblewrap 隔离代码执行 Runner 及部署
- [mcp.md](./integrations/mcp.md) — MCP Server 协议客户端
- [rag.md](./integrations/rag.md) — 知识检索（Qdrant/RagFlow）
- [redis-engine.md](./integrations/redis-engine.md) — Redis（Engine 视角：缓存、注册、锁）
- [matrix.md](./integrations/matrix.md) — Agent.Matrix 配置与权限只读引用
- [keycloak.md](./integrations/keycloak.md) — 本地 OIDC/JWT、Realm 与测试用户

## 其他

- [database.md](./database.md) — 数据存储唯一事实源（表、字段、索引、迁移）
- [decisions/](./decisions/) — 架构决策归档（ADR）
- [planning/](./planning/) — 进行中的规划（已完成工作不留单独文档）
- [trace-troubleshoot.md](./trace-troubleshoot.md) — Trace/Logs/Metrics 排障手册
- [parallel-previews.md](./parallel-previews.md) — 并行 worktree 预览实例机制

## 阅读路径

1. **首次阅读**：overview/SystemContext → Design → KeyFlows
2. **功能开发**：overview/Design → 对应 modules/ 域文件 → `.agent/rules/coding-conventions.md`
3. **数据库变更**：[database.md](./database.md) → overview/DataOwnership
4. **监控排障**：overview/Observability → trace-troubleshoot.md
