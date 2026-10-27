# 功能域索引

本目录每个业务域一份文档，域内功能点以章节组织。

| 域 | 说明 | 覆盖内容 |
|----|------|----------|
| [execution.md](execution.md) | 执行管线与核心调度 | 执行编排、流式输出、统一错误处理 |
| [conversation.md](conversation.md) | 会话记录与存储 | 持久化与写穿透、交互日志、上下文压缩、会话锁 |
| [engine.md](engine.md) | 运行时驱动、Host 与运行时管理 | IAgentRuntimeDriver 边界、HTTP/SSE 端点、全局错误处理、配置管理与热更新、健康检查、优雅停机、服务注册 |
| [capabilities.md](capabilities.md) | 能力集成 | 工具调用、Skill、MCP、RAG、CodeAct 代码执行 |
| [security.md](security.md) | 安全与租户 | 认证（OIDC/JWT/Basic/API Key）、权限评估、租户校验 |
| [chat-workspace.md](chat-workspace.md) | Gateway-first Chat 工作台 | 前端架构、Router 接口、开发与安全边界 |
| [router.md](router.md) | Router 服务发现、限流与就绪 | Redis 索引发现、故障回退、限流降级、ready 探测 |

## 阅读建议

1. 先看 execution.md 理解主执行链路
2. 再看 conversation.md 理解会话持久化与压缩
3. 按需查看 engine.md 和 capabilities.md 中的具体能力
4. security.md 与 execution.md 配套阅读
5. chat-workspace.md 记录浏览器经 Router 访问 Agent 平台的接口与安全边界；router.md 记录网关对 Redis 与下游 Engine 故障的处理策略
