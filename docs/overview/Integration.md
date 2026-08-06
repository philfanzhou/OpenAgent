# Integration — Agent.Core

## 集成矩阵

| 外部系统 | 接口类型 | 方向 | 用途 | 失败语义 |
|----------|----------|------|------|----------|
| LLM API（OpenAI/Azure/Anthropic/Gemini/兼容端点） | MAF + Provider SDK | 出 | `ChatClientAgent` 推理、流式与函数调用 | 保留 Provider 异常，请求失败 |
| MCP Server | Streamable HTTP / SSE | 出 | 工具发现与调用（McpServerClient） | 协议与传输生命周期由官方 MCP SDK 管理 |
| RAG - Qdrant | HTTP REST | 出 | 向量检索（QdrantAdapter） | 返回空结果，不中断主流程 |
| RAG - RagFlow | HTTP REST | 出 | 文档检索（RagFlowAdapter） | 返回空结果，不中断主流程 |
| Redis | TCP (StackExchange.Redis) | 出 | 会话热存储 | 读取失败返回 null/空列表；写入失败记录日志，不影响主流程 |
| SQL Server | ADO.NET (Microsoft.Data.SqlClient) | 出 | 会话冷归档 | 归档失败仅记录日志，热存储不受影响；支持指数退避重试 |

## 失败语义总结

- **LLM 调用失败**：请求直接失败，Pipeline 捕获异常后返回 `AgentResponse.Success=false`
- **MCP 连接失败**：抛出 ConnectionException，工具列表为空，不影响已有工具执行
- **MCP 调用超时**：30 秒超时，返回错误文本，不中断主流程
- **RAG 检索失败**：返回空结果，Agent 在无 RAG 增强情况下继续推理
- **Redis 不可用**：回退到 InMemoryConversationStore（开发/测试环境）
- **SQL Server 归档失败**：热存储一致，冷存储需要补偿；日志记录失败详情
