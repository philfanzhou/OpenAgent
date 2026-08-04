# LLM Provider 集成 — 任务清单

## 已完成

- [x] LLM Profile 注册、解析和 Agent 覆盖。
- [x] 生产调用统一到 MAF。
- [x] 八种 ApiFormat 的独立 client 构造。
- [x] OpenAI Responses 使用 Responses client。
- [x] Anthropic Messages 与 Gemini generateContent 官方 SDK 适配。
- [x] 真流式、函数调用、多模态和 usage 统一映射。
- [x] 删除自研 OpenAI HTTP/SSE/重试实现和 Semantic Kernel 引擎。

## 后续维护

- [ ] Provider SDK 升级时运行在线合约测试。
- [ ] Anthropic 集成稳定版发布后替换预览包。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
