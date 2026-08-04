# LLM Provider 集成 — 约定

## 配置约定

- Provider Profile 保存共享 endpoint/key/format，Agent 保存 provider 引用、model 和必要覆盖。
- 禁止日志输出 API key、Authorization header、附件字节和完整提示内容。
- 新 Provider 格式必须扩展 `ApiFormat`、工厂、RedisTool 类型和测试。

## 协议约定

- 优先使用协议所有者或 MAF 官方集成。
- 不把 Responses、Messages、generateContent 伪装成 Chat Completions。
- OpenAI-compatible 只用于明确兼容其请求与流事件语义的服务。
- Provider 错误保留原始异常链，不返回虚假的成功回答。

## 版本约定

- MAF 稳定包保持同一版本。
- Anthropic 预览包与 MAF 稳定版本成组升级。
- SDK 升级必须验证构造、函数循环、流式、多模态和 usage。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
