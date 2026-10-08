# Integration — OpenAgent

## 集成矩阵

| 外部系统 | 接口类型 | 方向 | 用途 | 失败语义 |
|----------|----------|------|------|----------|
| LLM API（OpenAI/Azure/Anthropic/兼容端点） | MAF + Provider SDK | 出 | `ChatClientAgent` 推理、流式与函数调用 | 保留 Provider 异常，请求失败 |
| MCP Server | Streamable HTTP / SSE | 出 | 官方 `McpClient` 发现并提供 `McpClientTool` | 协议、传输与工具调用生命周期由官方 MCP SDK 管理 |
| Agent Skill package | S3 directory objects + filesystem source | 出 | 官方 MAF `AgentSkillsProvider` 提供 `load_skill` / `read_skill_resource` | Web ZIP/MD 上传后在 OSS 中保存为解压目录；上传代码脚本不在宿主进程执行 |
| RAG - Qdrant | HTTP REST | 出 | 向量检索（QdrantAdapter） | 返回空结果，不中断主流程 |
| RAG - RagFlow | HTTP REST | 出 | 文档检索（RagFlowAdapter） | 返回空结果，不中断主流程 |
| PostgreSQL | Npgsql EF Core | 出 | 会话、消息、文件资产元数据与引用关系 | 持久化失败返回请求错误，不用内存数据替代 |
| S3-compatible object storage | S3 API | 出 | 文件原始字节、预览和下载 | 未配置或写入失败时文件端点返回依赖错误 |

## 联调与故障排查

超时、降级和错误行为统一查阅 [MCP](../modules/capabilities/mcp/README.md)、[工具调用](../modules/capabilities/tool-calling/README.md)、[错误处理](../modules/execution/errors/README.md)、[文件资产](../integrations/file-assets.md) 与 [集成索引](../integrations/README.md)。日志关联方式见 [可观测性](Observability.md)。
