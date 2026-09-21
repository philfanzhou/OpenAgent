# PR-6 报告：拆分 PlatformChatHistory，MAF 适配器回归薄编排层

- 分支：`refactor/split-platform-chat-history`（基于 `refactor/split-conversation-store`@0c8611c，堆叠在 PR-5 上）
- 类型：行为保持重构
- 依赖链：PR-4（record 化）→ PR-5（窄契约）→ 本 PR

## 动机

2026-09 会话架构审查（P0-1 项）：`PlatformChatHistory` 是 676 行的"上帝适配器"，一个类同时承担
五类职责：锁生命周期、历史加载与 tool_calls 修复、流式 partial 缓冲（由 AgentExecutor 从外部
推入）、文件驱动的消息构建（内联图片/附件重放）、持久化与状态迁移编排。近 120 commit 中该文件
被改 9 次；审查对照 MAF 官方设计指出"history provider 应只管加载/存储，流式状态混入持久化适配器
是边界错位"（MAF 三层管道：agent 中间件 / context+history / chat client 各司其职）。

## 拆分结果

`PlatformChatHistory` 619 行（PR-4 后）→ **375 行**（MAF `ChatHistoryProvider` 生命周期编排 + 持久化边界），
四个单一职责组件（`Backend/src/OpenAgent.Core/Conversation/`）：

| 组件 | 行数 | 职责 | 依赖 |
|------|------|------|------|
| `HistoryRepair.cs` | 95 | tool_calls 配对修复（折叠并行调用分片、丢弃未应答/重复调用）——**纯函数，无 I/O 无状态** | 无 |
| `StreamingTurnBuffer.cs` | 57 | 本轮已流出内容的累积（partial 正文/reasoning/工具调用与结果），供中断路径持久化时间线 | 无 |
| `ConversationTurnLock.cs` | 49 | 轮次锁生命周期：获取（冲突抛 `AgentException(Conflict)`）+ 三路径恰好一次释放 | `IConversationLock` |
| `HistoryFileInflater.cs` | 166 | 文件驱动的消息构建：当轮用户消息内联图片、历史附件重放与授权读取、降采样 | `IFileAssetService`、`IInlineImageOptimizer`、`FileAssetScope` |

设计要点：

1. **公开/内部 API 零变化**：`AppendPartial` / `AppendToolCall` / `CreateUserMessageAsync` /
   `BuildHistoryAsync` / `CompleteAsync` 保留为薄委托，`AgentExecutor`、`AgentExecutionScope` 与
   351 行的 `PlatformChatHistoryTests` 一行未改全部通过——这是行为保持的直接证据。
2. **职责边界**：`StreamingTurnBuffer` 只管"流出了什么"；序列号分配与落库留在
   `PlatformChatHistory`（`ConversationSessionStore` 仍是唯一持久化边界）。
3. `HistoryRepair` 纯函数化后可在无任何 mock 的情况下直接单测（后续补用例的最低成本接缝）。

## 改动清单

| 文件 | 改动 |
|------|------|
| `Backend/src/OpenAgent.Core/Conversation/PlatformChatHistory.cs` | 重写为编排器：组合四组件；删除内联的修复/缓冲/锁/文件内联实现 |
| `Backend/src/OpenAgent.Core/Conversation/HistoryRepair.cs` | 新增（自 PlatformChatHistory 原样迁移） |
| `Backend/src/OpenAgent.Core/Conversation/StreamingTurnBuffer.cs` | 新增（原 Append* 状态迁移） |
| `Backend/src/OpenAgent.Core/Conversation/ConversationTurnLock.cs` | 新增（原锁字段+ReleaseLockAsync 迁移，释放幂等语义显式化） |
| `Backend/src/OpenAgent.Core/Conversation/HistoryFileInflater.cs` | 新增（原 BuildHistoryAsync/AttachFilesAsync/ReadInline* 迁移；FileAssetScope 从会话上下文一次构造） |
| `Backend/tests/OpenAgent.Core.Tests/Conversation/ConversationTurnLockTests.cs` | 新增 3 用例：冲突抛 Conflict、双重释放只 Dispose 一次、未获取时释放为 no-op |

## 行为变化

无。所有迁移代码逐行保持；唯一语义显式化是锁释放幂等（原 `_released` 标志逻辑等价迁移到
`ConversationTurnLock`，并被新测试锁定）。

## 测试证据

- 基线（PR-5 后）：640 通过 / 0 失败 / 22 跳过
- 本 PR：`dotnet build` 0 错误；`dotnet test` **643 通过 / 0 失败 / 22 跳过**（+3 锁语义测试；
  Core 286→289；`PlatformChatHistoryTests` 未改动原样通过）

## 风险与回滚

- 风险：低。组件均 internal、仅被 PlatformChatHistory 组合；构造签名未变（DI/工厂无感）。
- 回滚：单 commit revert 恢复内联版本。
- 评审重点：`HistoryFileInflater` 的 `FileAssetScope` 从 `ConversationContext` 构造（原先由
  `CreateFileScope` 每次调用时构造，现在一次构造复用——语义相同，因 scope 值不可变）。

## 系列意义

至此 P0 三项（上帝适配器、DTO 镜像链、胖接口）全部偿还：典型会话功能改动的落点从
"PlatformChatHistory + 全链穿参"收敛到单一组件。后续 PR-7（TurnContext）与 PR-8（Metadata
类型化）的报告见系列目录。

## 评审整改（缩减准则）

按"除测试外代码应净缩减"准则复查：初版纯代码净变化 +102，压缩全部类级 XML 文档后为 **+84**，
构成全部为结构性脚手架，无任何新增逻辑：

- 4 个新文件的 using/命名空间/类声明/构造装配 ≈ 70 行（其中 HistoryFileInflater 主构造参数 8 行、ConversationTurnLock 7 行）；
- 保留既有内部 API（AppendPartial 等）的薄委托 14 行——换取 AgentExecutor/AgentExecutionScope/351 行既有测试零改动。

拆分类重构与"净缩减"存在结构性冲突（把一个文件拆成五个必然新增文件脚手架）；本 PR 已压至该下限。
若评审更优先缩减，可将 StreamingTurnBuffer/ConversationTurnLock 并回 PlatformChatHistory（约省 60 行），代价是失去独立可测性。
