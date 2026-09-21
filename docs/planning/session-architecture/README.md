# 会话架构重构系列（2026-09）

基于 2026-09 对最新 main（9b694ab）的会话架构审查：功能验证通过（构建 0 错误、638 测试全绿），
架构骨架健康（MAF-first、PostgreSQL 唯一事实源、写穿透缓存、乐观并发），但存在五处结构性债务
导致"改一个字段穿透 4~6 层、动辄 40+ 文件"（极端案例 `d449647`：41 文件）。

本系列按依赖顺序拆分为独立 PR，每个 PR 附详细说明报告，堆叠关系：

```text
main ─┬─ PR-1 统一 ChatRequest.Context 解析（Router/Engine，修复大小写不一致）
      ├─ PR-2 bwrap 沙箱参数单一事实源（Runner 安全参数去重）
      ├─ PR-3 移除 ArchivedAt 预留字段（ADR-0004）
      ├─ PR-4 会话 DTO record 化 + 映射收敛
      │     └─ PR-5 IConversationStore 拆分为 Reader/Writer
      │           └─ PR-6 PlatformChatHistory 职责拆分
      └─ PR-7+ 后续浪潮：TurnContext / Metadata 类型化 / AgentConfig 拆分 / 前端类型 codegen
```

| PR | 报告 | 状态 |
|----|------|------|
| PR-1 | [PR1-unify-conversation-id-parsing.md](./PR1-unify-conversation-id-parsing.md) | 已合并（#110） |
| PR-2 | [PR2-bwrap-args-single-source.md](./PR2-bwrap-args-single-source.md) | 已合并（#111） |
| PR-3 | [PR3-remove-conversation-archived-at.md](./PR3-remove-conversation-archived-at.md) | 已合并（#112） |
| PR-4 | [PR4-conversation-record-dtos.md](./PR4-conversation-record-dtos.md) | 待合并（#113） |
| PR-5 | [PR5-split-conversation-store.md](./PR5-split-conversation-store.md) | 本 PR（#114） |
| PR-6 | [PR6-split-platform-chat-history.md](./PR6-split-platform-chat-history.md) | 待合并（#115） |

> 本 README 为系列导航索引；各 PR 报告包含动机、改动清单、行为变化、测试证据与回滚方案。
> 注意：PR-1/2/3 从 main 分叉并行实施，本索引由各分支各自维护，合并时按行取并集。
