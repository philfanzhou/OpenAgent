# PR-5 报告：IConversationStore 拆分为 Reader / Writer 窄契约

- 分支：`refactor/split-conversation-store`（基于 `refactor/conversation-record-dtos`@164181e，堆叠在 PR-4 上）
- 类型：接口隔离重构，行为保持
- 上游依赖：PR-4；下游：PR-6（拆分后的 PlatformChatHistory 组件将依赖窄契约）

## 动机

2026-09 会话架构审查（P0-3 项）：`IConversationStore` 是 9 方法的胖接口，读写两侧能力
混在一个契约里。后果：

1. 纯读消费者（如 `ConversationAgentResolver` 只调 `GetRecordAsync`）被迫依赖写入方法，
   测试替身与 Mock 面被迫覆盖全接口；
2. 每个新存储特性都在同一接口上膨胀，三份实现（EF Core / 写穿透装饰器 / 内存测试版）
   同步改动面持续扩大；
3. 未来想为读路径引入独立缓存/副本策略时没有接缝。

对照微软 MAF 官方设计：MAF Hosting 层的 `AgentSessionStore` 抽象刻意保持极窄
（Save/Get/Delete 三方法），"session 存轻量会话态、history provider 每消息 append"两侧
分仓——窄契约 + 单一职责是官方反复强调的形状。

## 改动方案

### 契约（OpenAgent.Contracts/Conversation/）

| 文件 | 内容 |
|------|------|
| `IConversationReader.cs`（新） | 读侧 5 方法：GetMessagesAsync / GetMessagesPagedAsync / GetRecordAsync / ListConversationsAsync / SearchConversationsAsync |
| `IConversationWriter.cs`（新） | 写侧 5 方法：CreateAsync / AppendMessagesAsync / UpdateStatusAsync / SoftDeleteAsync / RecordCompressionAsync；`AppendResult` 移驻本文件（仅写侧使用） |
| `IConversationStore.cs` | 变为组合接口 `: IConversationReader, IConversationWriter`，不再声明自有方法；实现方零改动（已实现全部方法） |

### 消费方窄化（只改"确实只消费一侧"的类）

| 消费方 | 改动前 | 改动后 |
|--------|--------|--------|
| `ConversationAgentResolver`（归属解析） | IConversationStore | **IConversationReader**（纯读） |
| `ConversationQueryService`（查询门面） | IConversationStore | 保持不变（门面自有契约 IConversationQueryService 已隔离端点，评审整改中回退） |
| `ConversationSessionStore` / `AuditedCompactionStrategy` / `ConversationCompactionService` | IConversationStore | 保持不变（诚实表达"需要读写两侧"） |

### DI 接线

- Core `AddConversationServices`：`TryAddScoped` 把 `IConversationReader` / `IConversationWriter`
  转发到 `IConversationStore`（惰性解析，注册顺序无关）——所有调用 AddAgentCore 的宿主与
  测试主机自动获得窄契约。
- Infrastructure `AddOpenAgentInfrastructure`：同样的 TryAdd 转发（幂等），只组合
  Infrastructure 的宿主也能解析窄契约。
- 三契约在同一 scope 解析到**同一实例**，读写不会看到不同状态。

### 测试

- 新增 `ConversationStoreRegistrationTests`（Hosting.Tests，AddAgentCore + AddOpenAgentInfrastructure 真实组合）：验证
  Store/Reader/Writer 三契约同 scope 解析同一实例（覆盖无 Redis 分支）。
- `OpenAgent.Infrastructure.Tests.csproj` 显式引用 `Microsoft.Extensions.Configuration`
  （测试直接使用 ConfigurationBuilder，遵循"直接使用→直接引用"）。

## 行为变化

无。所有方法签名原样迁移；组合接口保证既有实现与 Mock 兼容。

## 测试证据

- 基线（PR-4 后）：639 通过 / 0 失败 / 22 跳过
- 本 PR：`dotnet build` 0 错误；`dotnet test` **640 通过 / 0 失败 / 22 跳过**
  （+1 注册测试；实施过程中曾出现 16 个 Core 测试失败——测试主机未注册窄契约，
  通过 Core 组合根转发修复，验证了"转发必须放在 Core 组合根"的必要性）

## 风险与回滚

- 风险：低。组合接口保留，外部消费者不受影响；转发注册惰性解析无顺序依赖。
- 回滚：单 commit revert。
- 遗留观察项：`IConversationQueryService` 契约中混入 `SoftDeleteAsync`（写操作出现在
  "Query" 命名契约中），属命名/职责遗留味道，建议在后续浪潮（见系列 README 的 PR-7+）里
  将其更名为会话目录服务或把 SoftDelete 移回命令侧。

## 给评审的提示

关注两点：① Infrastructure 与 Core 双处 TryAdd 转发是否接受（理由：两个组合根各自自洽）；
② ConversationQueryService 双参注入的取舍。
