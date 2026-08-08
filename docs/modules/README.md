# 功能域索引

本目录按业务域组织 Agent 平台的功能点文档。

## 业务域

| 域 | 说明 | 功能点 |
|----|------|--------|
| [execution/](./execution/) | 执行管线与核心调度 | pipeline, service, streaming, errors, conversation-lock |
| [conversation/](./conversation/) | 会话记录与存储 | store, context-compression |
| [engine/](./engine/) | 统一 SDK Runtime 与 Host 适配 | maf, host/chat-api, host/error-handling |
| [engine/runtime/](./engine/runtime/) | Engine 运行时管理 | config-management, config-hotreload, health-check, graceful-shutdown, service-registration, capability-registration |
| [capabilities/](./capabilities/) | 能力集成 | skill, tool-calling, mcp, rag |
| [security/](./security/) | 安全与租户 | auth, tenant, agent-id-validation, permission |
| [chat-workspace/](./chat-workspace/) | Gateway-first Chat 工作台 | chat, diagnostics, admin proxy, validation |

## 阅读建议

1. 先看 execution/pipeline 和 execution/service 理解主执行链路
2. 再看 conversation/store 理解会话持久化
3. 按需查看 engine/ 和 capabilities/ 下的具体能力
4. security/ 下的中间件文档与 execution/pipeline 配套阅读
5. chat-workspace/ 记录浏览器经 Router 联调的设计与验证证据
