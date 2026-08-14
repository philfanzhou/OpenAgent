# 集成点索引

本目录按外部系统组织集成文档。

| 集成点 | 说明 | 文档 |
|--------|------|------|
| LLM Provider | 大模型推理（OpenAI/Azure OpenAI/Anthropic） | [llm-provider/](./llm-provider/) |
| Agent.Matrix | 配置与权限管理（只读引用） | [matrix/](./matrix/) |
| Agent Provider | Router 意图识别与第三方 Agent 服务接入 | [agent-provider.md](./agent-provider.md) |
| File Assets | 独立用户文件、S3 兼容对象存储、预览与模型文件能力 | [file-assets.md](./file-assets.md) |
| PostgreSQL | EF Core 会话与文件资产持久化 | [../database/](../database/) |
| MCP Server | 外部工具协议（Model Context Protocol） | [mcp-server/](./mcp-server/) |
| RAG Service | 知识检索（Qdrant/RagFlow） | [rag-service/](./rag-service/) |
| Redis（Engine 视角）| 可选注册中心、Pub/Sub 与短生命周期协调 | [redis-engine/](./redis-engine/) |
