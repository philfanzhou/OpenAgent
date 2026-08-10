# 集成点索引

本目录按外部系统组织集成文档。

| 集成点 | 说明 | 文档 |
|--------|------|------|
| LLM Provider | 大模型推理（OpenAI/Azure/Anthropic/Gemini 等） | [llm-provider/](./llm-provider/) |
| Agent.Matrix | 配置与权限管理（只读引用） | [matrix/](./matrix/) |
| Agent Provider | Router 意图识别与第三方 Agent 服务接入 | [agent-provider.md](./agent-provider.md) |
| MCP Server | 外部工具协议（Model Context Protocol） | [mcp-server/](./mcp-server/) |
| RAG Service | 知识检索（Qdrant/RagFlow） | [rag-service/](./rag-service/) |
| Redis（Core 视角）| 会话热存储 | [redis/](./redis/) |
| Redis（Engine 视角）| 注册中心、Pub/Sub、配置热加载 | [redis-engine/](./redis-engine/) |
| SQL Server | 会话冷归档 | [sqlserver/](./sqlserver/) |
| SQLite | 会话冷归档 | [sqlite/](./sqlite/) |
