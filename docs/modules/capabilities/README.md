# capabilities — 能力集成

本目录按功能组织，不等同于单个程序集。独立业务能力位于 `OpenAgent.Capabilities`；MCP 与 Skill 适配位于 `OpenAgent.Application`；共享定义和目录接口位于 `OpenAgent.Contracts/Capabilities`。修改入口与依赖边界见 [开发指南](../../overview/DevelopmentGuide.md)。

| 功能点 | 说明 | 文档 |
|--------|------|------|
| skill | 技能发现、过滤与执行 | [skill/](./skill/) |
| tool-calling | 工具调用映射与循环 | [tool-calling/](./tool-calling/) |
| code-execution | CodeAct、隔离 Python 与文档文件生成 | [code-execution/](./code-execution/) |
| mcp | MCP 协议客户端集成 | [mcp/](./mcp/) |
| rag | RAG 检索适配器 | [rag/](./rag/) |
