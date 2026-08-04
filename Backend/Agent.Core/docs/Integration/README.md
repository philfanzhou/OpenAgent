# Agent.Core 集成点索引

本目录按外部系统组织集成文档。每个集成点包含 6 件套（01-FEATURE ~ 06-CONVENTIONS）。

| 集成点 | 说明 | 关键接口/类 | 文档 |
|--------|------|-------------|------|
| LLM Provider | 大模型推理（OpenAI/Azure/Anthropic/Gemini/兼容端点） | `ILlmRegistry` / `MafAgentFactory` / `MafChatClientFactory` | [llm-provider/](./llm-provider/) |
| Agent.Matrix | 配置与权限管理（只读引用） | `IAgentConfigProvider` / `AgentConfig` | [matrix/](./matrix/) |
| MCP Server | 外部工具协议（Model Context Protocol） | `IMcpClient` / `McpClient` | [mcp-server/](./mcp-server/) |
| RAG Service | 知识检索（Qdrant/RagFlow） | `IRagService` / `IRagAdapter` / `QdrantAdapter` / `RagFlowAdapter` | [rag-service/](./rag-service/) |
| Redis | 会话热存储 | `IConversationStore` / `RedisConversationStore` | [redis/](./redis/) |
| SQL Server | 会话冷归档 | `IConversationRepository` / `SqlServerConversationRepository` | [sqlserver/](./sqlserver/) |
| SQLite | 会话冷归档 | `IConversationRepository` / `SqliteConversationRepository` | [sqlite/](./sqlite/) |
