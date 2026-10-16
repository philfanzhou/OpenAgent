# 集成点索引

本目录按外部系统组织集成文档。

| 集成点 | 说明 | 文档 |
|--------|------|------|
| LLM Provider | 大模型推理（OpenAI/Azure OpenAI/Anthropic） | [llm-provider/](./llm-provider/) |
| 配置目录 / Matrix 接入边界 | 当前 Engine 配置读取与运行前解析；Matrix 为后续可替换来源 | [matrix/](./matrix/) |
| Agent Provider | Router 意图识别与第三方 Agent 服务接入 | [agent-provider.md](./agent-provider.md) |
| File Assets | 独立用户文件、S3 兼容对象存储、预览与模型文件能力 | [file-assets.md](./file-assets.md) |
| Code Runner | Bubblewrap 隔离代码执行、文档运行时及部署 | [code-runner.md](./code-runner.md) |
| PostgreSQL | EF Core 会话与文件资产持久化 | [../database/](../database/) |
| MCP Server | 外部工具协议（Model Context Protocol） | [mcp-server/](./mcp-server/) |
| ACP .NET SDK | 单包 SDK 规格、协议基线、控制台与阶段验收 | [acp-dotnet-sdk/](./acp-dotnet-sdk/) |
| RAG Service | 知识检索（Qdrant/RagFlow） | [rag-service/](./rag-service/) |
| Redis（Engine 视角）| 可选注册中心、Pub/Sub 与短生命周期协调 | [redis-engine/](./redis-engine/) |
| Keycloak | 本地 OIDC/JWT、Realm、SPA Client 与测试用户 | [keycloak/](./keycloak/) |
