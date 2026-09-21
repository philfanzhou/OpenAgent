# PR-4 报告：会话 DTO record 化，消灭手写整对象复制

- 分支：`refactor/conversation-record-dtos`（基于 main@9b694ab）
- 类型：行为保持重构（含一处刻意的缺陷修正，见"行为变化"）
- 上游依赖：无（与 PR-1/2/3 并行）；下游：PR-5、PR-6 基于本分支堆叠

## 动机

2026-09 会话架构审查（P0-2 项）发现：`ConversationMessage` / `ConversationRecord` 是 init-only class，
"改一个字段"需要整对象重建，导致 5 处逐字段手写复制散落在 4 个文件中。每新增一个 DTO 字段
（如 `d449647` 加 TraceId 时），所有复制点都要同步加一行——漏加即静默丢字段，这正是"字段级
变更穿透多层"的直接成因之一。

### 复制点清单（改动前）

| # | 位置 | 复制内容 |
|---|------|----------|
| 1 | `ConversationSessionStore.WithTraceId`（原 :214-229） | 全字段 + TraceId |
| 2 | `PlatformChatHistory.WithCompletion`（原 :644-661） | 全字段 + TokenUsage/ModelId |
| 3 | `AgentMessageAdapter.AssociateFiles`（原 :224-241） | 全字段 + Metadata/FileIds |
| 4 | `InMemoryConversationStore.StripMessages`（原 :200-220） | 全字段 + 置空集合 |
| 5 | `ConversationSessionStore.RetryAppendAsync`（原 :269-281） | **经 `Message()` 工厂重建**，静默丢弃 MessageId/IdempotencyKey/Timestamp |

`#5` 已构成实际缺陷：`IdempotencyKey` 是幂等去重键（InMemory store 按 it 去重、DB 有唯一约束设计），
乐观锁冲突重试时却把它丢掉，使重试批次绕过幂等保护。

## 改动方案

1. `ConversationMessage` / `ConversationRecord`：`sealed class` → `sealed record`。属性访问器
   全部保持不变（init/set 原样），构造语法（对象初始化器）完全兼容，序列化（Redis JSON 热副本）
   行为不变。
2. 五处复制点全部替换为 `with` 表达式，只声明真正变化的字段（净删约 90 行样板）：
   - `message with { TraceId = context.TraceId }`
   - `_pending[assistantIndex] with { TokenUsage = usage, ModelId = modelId }`
   - `message with { Metadata = metadata, FileIds = ... }`
   - `r with { Messages = [], ContextSummaries = [] }`
   - 重试路径：`message with { Sequence = ..., TraceId = ... }`

### 行为变化（唯一一处，刻意为之）

`RetryAppendAsync` 从"工厂重建"改为 `with` 后，冲突重试批次**保留原 MessageId、Timestamp、
IdempotencyKey**，只重排 Sequence 并补盖 TraceId。理由：重试是同一批逻辑消息的重排序，重新生成
标识（旧代码行为）会让幂等去重失效；保留时间戳也更符合"消息产生时刻"语义。已新增回归测试锁定：
`SaveAsync_VersionConflictRetry_PreservesMessageIdentityAndIdempotencyKey`
（`Backend/tests/OpenAgent.Core.Tests/Conversation/ConversationSessionStoreTests.cs`）。

### 安全性论证

- record 化引入值相等语义（Equals/GetHashCode）。全仓 grep 确认无 `ReferenceEquals`/`HashSet`/
  字典键使用这两个类型（`ChatMessage` 的 ReferenceEquals 属 MEF 类型，无关）。
- EF 实体（`ConversationMessageEntity`）与 DTO 是不同类型，实体映射（`ToMessagesAsync` 等）不受
  影响；该映射属于必要的类型转换，非复制冗余，保留原位。
- Redis 热副本反序列化：`required` 属性在 class 上已存在，record 化不改变 STJ 契约；旧缓存 JSON
  含未知字段时 STJ 默认忽略。

## 改动清单

| 文件 | 改动 |
|------|------|
| `Backend/src/OpenAgent.Contracts/Conversation/ConversationMessage.cs` | class → record |
| `Backend/src/OpenAgent.Contracts/Conversation/ConversationRecord.cs` | class → record |
| `Backend/src/OpenAgent.Core/Conversation/ConversationSessionStore.cs` | 删 WithTraceId；SaveAsync 用 with；RetryAppendAsync 改 with 并保留标识 |
| `Backend/src/OpenAgent.Core/Conversation/PlatformChatHistory.cs` | 删 WithCompletion，CompleteAsync 用 with |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentMessageAdapter.cs` | AssociateFiles 用 with |
| `Backend/src/OpenAgent.Core/Conversation/Store/InMemoryConversationStore.cs` | StripMessages 用 with |
| `Backend/tests/OpenAgent.Core.Tests/Conversation/ConversationSessionStoreTests.cs` | 新增重试身份保留回归测试 |

## 测试证据

- 基线（main@9b694ab）：638 通过 / 0 失败 / 22 跳过
- 本 PR：`dotnet build` 0 错误；`dotnet test` **639 通过 / 0 失败 / 22 跳过**（+1 新回归测试；
  其余全部保持原行为通过——尤其 `PlatformChatHistoryTests`（351 行）与 `InMemoryConversationStoreTests`
  覆盖了被改写的复制路径）

## 风险与回滚

- 风险：低。值相等语义无消费者；重试标识保留是语义增强且被测试锁定。
- 回滚：单 commit revert；`with` 表达式回到手写复制即可。
- 对后续 PR 的意义：PR-5（拆 IConversationStore）与 PR-6（拆 PlatformChatHistory）将在本分支
  之上进行，复制样板消失后拆分时的字段同步面更小。

## 给评审的提示

重点看 `RetryAppendAsync` 的行为变化是否接受（保留 vs 重新生成 MessageId）；其余改动机械等价。
