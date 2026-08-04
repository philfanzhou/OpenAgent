# MAF Runtime — 测试

本次架构删除了 Engine 请求/响应合同，依赖这些类型的旧测试不再作为兼容要求。

后续测试直接围绕 MAF 原生边界重写：

- fake `IChatClient` 驱动 `ChatClientAgent`；
- `AIContextProvider` 能力发现与授权执行；
- `ChatHistoryProvider` 锁、历史、成功/失败写回；
- `CompactionProvider` 消息组压缩；
- `AgentSession` 工具循环与流式响应。

生产 Core 与 Engine Host 必须保持 0 warning 编译；真实 Provider、Redis、MCP E2E
单独验证。
