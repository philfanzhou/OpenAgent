# capabilities — 能力集成

| 功能点 | 说明 | 文档 |
|--------|------|------|
| skill | 技能发现、过滤与执行 | [skill/](./skill/) |
| tool-calling | 工具调用映射与循环 | [tool-calling/](./tool-calling/) |
| code-execution | CodeAct、隔离 Python 与文档文件生成 | [code-execution/](./code-execution/) |
| mcp | MCP 协议客户端集成 | [mcp/](./mcp/) |
| rag | RAG 检索适配器 | [rag/](./rag/) |

## 注册与参数读取

`AddAgentCore` 的服务通过 `TryAdd` 注册，多实现的能力源、RAG Adapter 和配置验证器通过
`TryAddEnumerable` 去重。重复注册不会增加工具源或命名 HTTP 客户端的 User-Agent，
调用方预先注册的实现继续生效。FileAsset 与 Workspace 共用内部 `ToolArguments`
读取标量；整数采用固定文化解析，工具自身保留必填项、范围和默认值校验。
