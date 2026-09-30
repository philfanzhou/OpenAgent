# Agent 压缩触发机制调研

调研日期：2026-09-30。产品行为依据官方资料；OpenAgent 的取舍是本次实现的工程决策。

| Agent | 自动触发 | 手动触发 | 可复用原则 |
|---|---|---|---|
| Codex | `model_auto_compact_token_limit` 控制 token 阈值，未设置时使用模型默认值；可配置统计完整上下文或压缩窗口前缀之后的增长 | CLI `/compact` 将早期轮次替换为摘要 | 模型窗口与当前上下文决定触发，累计计费量不等于上下文水位 |
| Claude Code | 接近窗口限制时管理上下文，先清理旧工具结果，再按需摘要；大结果反复填满窗口时会停止连续自动压缩 | `/compact` 可指定关注内容，项目规则可提供 Compact Instructions | 保留目标，将持久规则与摘要分开，避免无收益重复压缩 |
| OpenCode | `compaction.auto` 默认开启，`reserved` 留出空间；`prune` 独立控制工具输出清理，当前文档默认关闭 | 会话摘要与自动模式共用压缩流程，源码 `process` 接收 `auto` 标记 | 留出压缩空间；工具清理和摘要分开，摘要必须进入后续请求 |

来源：

- [Codex 配置参考](https://developers.openai.com/codex/config-reference/)
- [Codex CLI slash commands](https://developers.openai.com/codex/cli/slash-commands/)
- [Claude Code：When context fills up](https://code.claude.com/docs/en/how-claude-code-works#when-context-fills-up)
- [OpenCode：Compaction](https://opencode.ai/docs/config/#compaction)
- [OpenCode 压缩源码](https://github.com/anomalyco/opencode/blob/dev/packages/opencode/src/session/compaction.ts)

## OpenAgent 的取舍

自动模式在后端每次模型调用前检查，包括工具循环。消息、指令和工具定义纳入估算，默认 80% 触发，另外预留输出空间。手动模式复用策略而不等待阈值。当前用户请求和最新完整工具组是保留底线；近期轮次是优先保留项，预算不足时可进一步摘要较早轮次。完整消息用于审计，持久化投影用于后续调用。

单个大工具结果与历史累计是不同问题。本次保留现有工具结果协议；摘要输入过大时分段处理全部候选内容并滚动合并。摘要失败或没有有效收益时恢复原上下文，记录失败或跳过，不伪造成功。
