# 会话架构重构系列（2026-09）

基于 2026-09 对 main（9b694ab）的会话架构审查：功能验证通过（构建 0 错误、测试全绿），
架构骨架健康（MAF-first、PostgreSQL 唯一事实源、写穿透缓存、乐观并发），但存在结构性债务
导致"改一个字段穿透多层、动辄数十文件"（极端案例 `d449647`：后端 41 文件 +
前端 `9ede3e1` 追随手改）。系列按依赖顺序拆分为独立 PR，每个 PR 附详细规划/实施报告。

```text
main ─┬─ PR-1 统一 ConversationId 解析（Router/Engine）
      ├─ PR-2 bwrap 沙箱参数单一事实源（Runner）
      ├─ PR-3 移除 ArchivedAt 预留字段（ADR-0004）
      ├─ PR-4 会话 DTO record 化 + 映射收敛
      │     └─ PR-5 IConversationStore 拆分为 Reader/Writer
      │           └─ PR-6 PlatformChatHistory 职责拆分
      └─ 后续浪潮（本分支规划）
            ├─ PR-7 统一轮次上下文 TurnContext
            ├─ PR-8 会话消息 Metadata 类型化
            ├─ PR-9 AgentConfig 拆分
            └─ PR-10 前端类型代码生成
```

## 全系列索引

| PR | 主题 | 报告 | 实施分支 | 状态 |
|----|------|------|----------|------|
| PR-1 | 统一 ConversationId 解析（Router/Engine 大小写不一致） | [PR1-unify-conversation-id-parsing.md](./PR1-unify-conversation-id-parsing.md) | `refactor/unify-conversation-id-parsing` | 已实施（待合并） |
| PR-2 | bwrap 沙箱参数单一事实源 | [PR2-bwrap-args-single-source.md](./PR2-bwrap-args-single-source.md) | `refactor/bwrap-args-single-builder` | 已实施（待合并） |
| PR-3 | 移除 ArchivedAt 预留字段（ADR-0004） | [PR3-remove-conversation-archived-at.md](./PR3-remove-conversation-archived-at.md) | `docs/adr-conversation-archived-at` | 已实施（待合并） |
| PR-4 | 会话 DTO record 化 + 映射收敛 | [PR4-conversation-record-dtos.md](./PR4-conversation-record-dtos.md) | `refactor/conversation-record-dtos` | 已实施（待合并） |
| PR-5 | IConversationStore 拆分为 Reader/Writer | [PR5-split-conversation-store.md](./PR5-split-conversation-store.md) | `refactor/split-conversation-store` | 实施中 |
| PR-6 | PlatformChatHistory 职责拆分 | [PR6-split-platform-chat-history.md](./PR6-split-platform-chat-history.md) | `refactor/split-platform-chat-history` | 实施中 |
| PR-7 | 统一轮次上下文 TurnContext | [PR7-turn-context.md](./PR7-turn-context.md) | — | 规划 |
| PR-8 | 会话消息 Metadata 类型化 | [PR8-typed-message-metadata.md](./PR8-typed-message-metadata.md) | — | 规划 |
| PR-9 | AgentConfig 拆分 | [PR9-split-agent-config.md](./PR9-split-agent-config.md) | — | 规划 |
| PR-10 | 前端类型代码生成 | [PR10-frontend-type-codegen.md](./PR10-frontend-type-codegen.md) | — | 规划 |

> **分支维护说明**：PR-1~6 的报告创建于各自实施分支，本 worktree（基于 main@9b694ab）
> 只包含 PR-7~10 的规划报告，上表 PR-1~6 的链接在合并进 main 前于本分支不可解析。
> 各分支的系列 README 各自维护，**合并时取并集**（按行去重，状态列以最新为准）。

## 后续浪潮（PR-7~10）速览

| PR | 解决的债务 | 关键证据 | 规模预估 |
|----|------------|----------|----------|
| PR-7 | 一次轮次拼装三套并行 scope（Capture/FileScope/ConversationContext），新横切字段需穿工厂层 | `AgentFactory.cs:44-77` | 3 个子 PR，Core 内部 |
| PR-8 | `ConversationMessage.Metadata` 为裸 string-dict，Files 清单 JSON 套 JSON，前后端魔法字符串契约 | `ConversationMessage.cs:17`、`api.ts:248-271` | 3+ 个子 PR，含数据兼容窗口 |
| PR-9 | `AgentConfig` 被 6 个 src 项目 16 个文件精确引用；`ICapabilitySource` 整只传入 | `ICapabilitySource.cs:6-13` | 3 个子 PR，零 DB migration |
| PR-10 | `types.ts` 358 行手写镜像 C# Contracts，无漂移门禁（TraceId 已实际漂移） | `types.ts:25-43` vs `ConversationMessage.cs:16` | 3 个子 PR，CI 门禁 |

## 建议实施顺序

PR-5/6 合并 → PR-7（TurnContext，为 8/9 减少同文件冲突）→ PR-8（Metadata 类型化，
前后端契约先稳）→ PR-9（AgentConfig 拆分）→ PR-10（类型 codegen，吃下 8/9 之后的
终态契约）。PR-7 与 PR-8 也可并行（文件交集有限），PR-10c 的漂移清偿建议压轴。
