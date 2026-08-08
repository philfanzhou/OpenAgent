# 集成点索引

本目录按外部系统组织集成文档。

| 集成点 | 说明 | 文档 |
|--------|------|------|
| LLM Provider | 大模型推理（OpenAI/Azure/Anthropic/Gemini 等） | [llm-provider/](./llm-provider/) |
| Agent.Matrix | 配置与权限管理（只读引用） | [matrix/](./matrix/) |
| External Agent | Router 统一选路与第三方 OpenAgent 协议转发 | [external-agent.md](./external-agent.md) |
| S3 Object Storage | multipart 附件原始字节的可选持久化 | [object-storage.md](./object-storage.md) |
| MCP Server | 外部工具协议（Model Context Protocol） | [mcp-server/](./mcp-server/) |
| RAG Service | 知识检索（Qdrant/RagFlow） | [rag-service/](./rag-service/) |
| Redis（Core 视角）| 会话热存储 | [redis/](./redis/) |
| Redis（Engine 视角）| 注册中心、Pub/Sub、配置热加载 | [redis-engine/](./redis-engine/) |
| SQL Server | 会话冷归档 | [sqlserver/](./sqlserver/) |
| SQLite | 会话冷归档 | [sqlite/](./sqlite/) |
