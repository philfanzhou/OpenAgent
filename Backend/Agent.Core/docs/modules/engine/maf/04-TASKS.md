# MAF Engine — 任务清单

## 已完成

- [x] MAF 成为唯一生产 Agent 引擎；保留 Mock 测试引擎。
- [x] `RunAsync` 与 `RunStreamingAsync` 真实执行。
- [x] `FunctionInvokingChatClient` 接管函数调用循环和最大迭代次数。
- [x] 精确 JSON Schema 的可执行 `AIFunction` 适配器。
- [x] 工具执行回接权限、MCP、Skill、RAG、审计、遥测和持久化。
- [x] 删除 OpenAIDriver、SemanticKernel 生产实现和重复测试。
- [x] 旧 framework 配置透明映射到 MAF。
- [x] OpenAI Chat、Responses、Azure、Anthropic、Gemini、Ollama、LM Studio、OpenWebUI 客户端。
- [x] 图片、PDF 和 UTF-8 文本文件输入。
- [x] multipart 同步与 NDJSON 流式附件端点。
- [x] Agent、Model、Tool、Function、MCP、Skill 六维授权扩展点。

## 后续可选增强

- [ ] 在 Host 前增加 magic-byte 检测、病毒扫描和租户对象存储。
- [ ] 对真实生产凭据执行各 Provider 的长期合约回归。
- [ ] Anthropic MAF 集成发布稳定版后移除预览包。
- [ ] 业务启用多 Agent 时再评估 MAF Workflow；当前 `Agent.Workflow` 仍不是运行服务。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
