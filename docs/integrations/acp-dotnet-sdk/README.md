# C# ACP SDK

| 文档 | 内容 |
|---|---|
| [架构与目录](DESIGN.md) | 单包边界、协议类型、并发、资源所有权与扩展 |
| [使用指南](USAGE.md) | Client / Agent 接入与回调 |
| [八阶段任务](IMPLEMENTATION.md) | P0–P7 的实现范围与逐步验收 |
| [固定来源](sources.json) | 协议 schema 的版本与许可证来源 |

当前状态统一见 [任务清单](../../planning/TODO.md)。建议先看架构，再看使用指南；验证入口为 `dotnet test Backend/tests/OpenAgent.Runtime.Acp.Tests`。
平台如何消费本 SDK 见 [OpenCode ACP 运行时](../opencode-runtime.md)；平台适配类库不增加 SDK NuGet 包。
