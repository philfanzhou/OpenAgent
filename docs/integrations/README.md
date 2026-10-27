# 集成点索引

本目录按外部系统组织集成文档。

| 集成点 | 说明 | 文档 |
|--------|------|------|
| OpenCode ACP 运行时 | 平台执行、工具桥接、配置与验证范围 | [opencode-runtime.md](opencode-runtime.md) |
| ACP SDK | 单包 C# 协议、Client/Agent、进程、八阶段与验证 | [acp-dotnet-sdk](acp-dotnet-sdk/README.md) |
| LLM Provider | 大模型推理（OpenAI/Azure OpenAI/Anthropic） | [llm-provider](llm-provider.md) |
| 配置目录 / Matrix 接入边界 | 当前 Engine 配置读取与运行前解析；Matrix 为后续可替换来源 | [matrix](matrix.md) |
| Agent Provider | Router 意图识别与第三方 Agent 服务接入 | [agent-provider.md](./agent-provider.md) |
| File Assets | 独立用户文件、S3 兼容对象存储、预览与模型文件能力 | [file-assets.md](./file-assets.md) |
| Code Runner | Bubblewrap 隔离代码执行、文档运行时及部署 | [code-runner.md](./code-runner.md) |
| PostgreSQL | EF Core 会话与文件资产持久化 | [../database/](../database/) |
| MCP Server | 外部工具协议（Model Context Protocol） | [mcp-server](mcp-server.md) |
| RAG Service | 知识检索（Qdrant/RagFlow） | [rag-service](rag-service.md) |
| Redis（Engine 视角）| 可选注册中心、Pub/Sub 与短生命周期协调 | [redis-engine](redis-engine.md) |
| Keycloak | 本地 OIDC/JWT、Realm、SPA Client 与测试用户 | [keycloak](keycloak.md) |
