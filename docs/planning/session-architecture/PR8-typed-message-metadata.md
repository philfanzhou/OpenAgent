# PR-8：会话消息 Metadata 类型化

| 项 | 内容 |
|----|------|
| 状态 | 规划（2026-09 会话架构审查后续浪潮） |
| 基线 | main@9b694ab |
| 类型 | 契约重构 + 持久化兼容读取，**含数据兼容窗口** |
| 依赖 | 无硬依赖；建议在 PR-4（会话 DTO record 化）之后实施，复用其测试基建 |

## 1. 动机

### 1.1 类型逃逸：结构化数据被塞进 string-dict

`ConversationMessage.Metadata` 是裸字典
（`Backend/src/OpenAgent.Contracts/Conversation/ConversationMessage.cs:17`）：

```csharp
public IReadOnlyDictionary<string, string>? Metadata { get; init; }
```

当前在用的字符串键（写/读两侧均已核对）：

| 键 | 写入 | 读取 | 载荷形态 |
|----|------|------|----------|
| `Files` | `AgentMessageAdapter.BuildFileMetadata`（`AgentMessageAdapter.cs:190-209`，JSON 序列化匿名对象数组塞进 :200-208）；`AssociateFiles`（:211-242，:223 复制键） | 前端 `normalizeConversation`（`Frontend/OpenAgent.Chat/src/api.ts:252-265`，`JSON.parse(raw)`，还要兼容 camelCase/PascalCase 双形态 :257-262） | **JSON 字符串套在 string 值里** |
| `ToolArguments` | `CreateToolMetadata`（`AgentMessageAdapter.cs:317-325`） | `FromStored`（:62-68）`ParseArguments`；前端 `messagePresentation.ts:142`（`parseToolArguments`，:316） | JSON 字符串 |
| `Reasoning` | `CreateMessageMetadata`（:327-342）；`BuildPartialMessage`（`PlatformChatHistory.cs:174`） | `FromStored`（:77-82）；前端 `api.ts:253` | 纯文本 |
| `ExecutionStatus` | `BuildPartialMessage`（`PlatformChatHistory.cs:165-182`，:170 写 `status.ToString()`） | 前端 `ChatMessages.vue:104-105`（字符串比较 `'Cancelled'`/`'Failed'`） | 枚举名字符串 |

问题：

- **类型逃逸**：`Files` 是 `List<{fileId,fileName,mediaType,length,objectKey}>` 被双重序列化
  （对象 → JSON 字符串 → 再进字典）。字段拼错（如 `objectKey` vs `ObjectKey`）只能运行时发现，
  前端被迫做双命名兼容（`api.ts:257-262`）。
- **前后端隐式契约**：键名是魔法字符串，无编译期保护；新增元数据字段 = 两边各改一处、
  靠人肉对齐（`d449647` 后端加 TraceId 走列、前端 `9ede3e1` 补 UI 即为此模式的实例）。
- **静默数据丢失**：EF 反序列化失败时直接吞掉
  （`EfCoreConversationStore.cs:455-468`，`catch (JsonException) { return null; }`），
  历史消息的 Files/Reasoning 会在格式异常时无声消失。

### 1.2 三套持久化语义并存（P0 风险背景）

| 存储 | 序列化 | 位置 |
|------|--------|------|
| EF/PostgreSQL | `MetadataJson` jsonb 列，**默认 JsonSerializerOptions**（属性保持 CLR 命名） | `OpenAgentDbContext.cs:53`；写 `EfCoreConversationStore.cs:360`；读 :455-468 |
| Redis 热缓存 | 整个 `ConversationRecord`，**camelCase NamingPolicy** | `RedisConversationCache.cs:16-19,33,39`（注意：naming policy 不改字典键，今天 dict 键侥幸一致） |
| InMemory（测试/降级） | **不序列化**，直接持有对象图 | `InMemoryConversationStore.cs:8-10` |

一旦 Metadata 从 `Dictionary<string,string>` 换成带属性的强类型对象，"EF 默认命名 vs Redis
camelCase" 会立刻对 **属性名** 产生分歧——这是审查点名的 P0：**InMemory/EF/Redis 三侧序列化
语义必须一致**，否则同一消息经缓存命中与 DB 直读会得到不同形状。

## 2. 方案设计

### 2.1 目标类型

```csharp
// Backend/src/OpenAgent.Contracts/Conversation/ConversationMessageMetadata.cs
public sealed class ConversationMessageMetadata
{
    /// 附件清单（原 "Files" 键内的 JSON 数组提升为强类型）。
    public List<MessageFileMetadata>? Files { get; set; }
    /// 思考链文本（原 "Reasoning"）。
    public string? Reasoning { get; set; }
    /// 中止/失败状态（原 "ExecutionStatus"，ConversationStatus 的字符串名）。
    public string? ExecutionStatus { get; set; }
    /// 工具调用参数的原始 JSON（保持字符串，消费端各自 ParseArguments）。
    public string? ToolArguments { get; set; }
    /// 未识别键的逃生舱：旧数据/第三方写入的透传。
    public Dictionary<string, string>? Extensions { get; set; }
}

public sealed record MessageFileMetadata(
    string FileId, string FileName, string MediaType, long Length, string? ObjectKey);
```

`ConversationMessage.Metadata` 类型改为 `ConversationMessageMetadata?`。

设计取舍：

- **保留 `Extensions` 逃生舱**而非 `JsonExtensionData` 全量透传：显式白名单已知键，
  未知键原样保留，避免强类型化把第三方元数据丢掉。
- `ExecutionStatus` 保持 `string`（值域 = `ConversationStatus.ToString()`）而非引入枚举：
  `ConversationStatus` 本身在契约里是数值枚举 + 字符串名双形态（`types.ts:1` 前端已按
  `'Running' | 'Completed' | ... | number` 建模），此处先不做值域收紧，留给后续 PR。
- `ToolArguments` 维持 JSON 字符串：它是"模型产物的透传载荷"而非平台结构，
  ParseArguments（`AgentMessageAdapter.cs:344-359`）已容忍解析失败。

### 2.2 双形态读取（新旧共存窗口）

EF 侧自定义转换器，同一 jsonb 列支持两种形态：

```csharp
// 读：探测形态 —— 对象含 "files"/"reasoning"/... 已知属性名（camelCase，与 Redis 对齐）
//     或旧形态（任意 string→string 字典）
// 写：始终写新形态，统一使用 JsonSerializerDefaults.Web（camelCase）
internal sealed class ConversationMessageMetadataConverter :
    JsonConverter<ConversationMessageMetadata?>
{
    // Read:
    //   1) StartObject 且首个属性名命中已知键集合 → 读新形态；
    //   2) 否则尝试 Dictionary<string,string> → 映射：
    //        "Files"→Files(再 Parse 一次内层 JSON)、"Reasoning"→Reasoning、
    //        "ToolArguments"→ToolArguments、"ExecutionStatus"→ExecutionStatus、
    //        其余 → Extensions；
    //   3) 解析失败不再静默：降级为 Extensions 原样保留原始键值并打 Warning 日志。
    // Write: 新形态 + camelCase。
}
```

关键决策：**写路径统一 camelCase**，并同步给 EF 的 `MetadataJson` 序列化显式传入
`JsonSerializerDefaults.Web`（替换 `EfCoreConversationStore.cs:360` 的默认 options），
Redis 缓存（已是 camelCase）与 EF 从此同语义；InMemory 无序列化天然兼容。
旧数据（PascalCase 字典）在读取时兼容，不迁移、不回填。

前端 `types.ts` 同步：

```typescript
// types.ts（生成或手写，见 PR-10）
export interface ConversationMessageMetadata {
  files?: MessageFileMetadata[]
  reasoning?: string
  executionStatus?: string
  toolArguments?: string
  extensions?: Record<string, string>
}
export interface ConversationMessage { /* ... */ metadata?: ConversationMessageMetadata }
```

`normalizeConversation`（`api.ts:248-271`）退化为：仅把 `metadata.files` 透传为顶层
`files`、`metadata.reasoning` → `reasoning` 的 UI 投影，删除 `JSON.parse` 与双命名兼容。

## 3. 影响面清单（文件级）

| 文件 | 变化 |
|------|------|
| `Backend/src/OpenAgent.Contracts/Conversation/ConversationMessageMetadata.cs` | 新增 |
| `Backend/src/OpenAgent.Contracts/Conversation/ConversationMessage.cs` | `Metadata` 类型替换（:17） |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentMessageAdapter.cs` | `BuildFileMetadata`（:190-209）、`AssociateFiles`（:211-242）、`CreateToolMetadata`（:317-325）、`CreateMessageMetadata`（:327-342）、`FromStored`（:62-68, :77-82）全部改读写强类型 |
| `Backend/src/OpenAgent.Core/Conversation/PlatformChatHistory.cs` | `BuildPartialMessage`（:165-182）改强类型；:614/:640/:657 调用点 |
| `Backend/src/OpenAgent.Core/Conversation/ConversationSessionStore.cs` | `Message` 工厂参数（:188-212）与 WithTraceId 克隆（:214 起）适配 |
| `Backend/src/OpenAgent.Infrastructure/Conversations/EfCoreConversationStore.cs` | :360 写序列化换 Web options + 转换器；:455-468 `DeserializeMetadata` 重写为双形态读取，失败降级 Extensions + 日志 |
| `Backend/src/OpenAgent.Infrastructure/Conversations/RedisConversationCache.cs` | 序列化随契约类型走，无代码改动，但需 round-trip 测试覆盖 |
| `Backend/src/OpenAgent.Core/Conversation/Store/InMemoryConversationStore.cs` | 无代码改动（对象直存），列入三态一致性测试矩阵 |
| `Frontend/OpenAgent.Chat/src/types.ts` | `metadata` 类型化（:33）；`MessageFile` 与 `MessageFileMetadata` 关系收敛（:57-66） |
| `Frontend/OpenAgent.Chat/src/api.ts` | `normalizeConversation` 简化（:248-271） |
| `Frontend/OpenAgent.Chat/src/messagePresentation.ts` | `parseToolArguments(message.metadata?.ToolArguments)`（:142）改为读 `metadata?.toolArguments` |
| `Frontend/OpenAgent.Chat/src/components/ChatMessages.vue` | `metadata?.ExecutionStatus`（:104-105）改小写字段 |
| `Backend/tests/OpenAgent.Contracts.Tests/Serialization/ContractSerializationTests.cs` | 双形态序列化测试 |
| `Backend/tests/OpenAgent.Infrastructure.Tests/**`（会话存储测试） | jsonb 读写回归 |
| `Backend/tests/OpenAgent.Core.Tests/Conversation/**` | 消息工厂/历史重建回归 |
| `Frontend/OpenAgent.Chat/src/api.test.ts`、`messagePresentation.test.ts` | 归一化/展示回归 |
| `docs/database/tables/ConversationMessages.md` | MetadataJson 形态说明更新（双形态窗口 + 终态） |

## 4. 分阶段实施步骤

1. **PR-8a（后端读兼容）**：新增类型 + 转换器；EF 读侧支持双形态、写侧仍写旧形态
   （dict）。此时旧消费者（含未升级前端）完全无感。补三存储 round-trip 测试矩阵
   （EF jsonb / Redis camelCase / InMemory）。
2. **PR-8b（后端写切换）**：写路径切新形态（camelCase 对象）；`AgentMessageAdapter`/
   `PlatformChatHistory` 产出强类型；EF 写 options 显式 Web。部署后新旧形态在库中并存，
   读侧兼容。
3. **PR-8c（前端切换）**：`types.ts` metadata 类型化、`api.ts`/`ChatMessages.vue`/
   `messagePresentation.ts` 消费新形态。前端与 8b 后端可分开发布（8b 之后旧前端仍能工作，
   因为 `metadata.files` 缺失时 `normalizeConversation` 走 `!raw` 分支返回原消息——
   需在 8b 中保留 `Files`→前端的过渡输出或确认发布顺序，见风险表）。
4. **PR-8d（收口，可选、滞后一个发布窗口）**：观测确认无旧形态写入后，考虑一次性
   `UPDATE ... SET metadata_json` 数据归一（纯形态转换，无信息变化），并移除双形态读取的
   转换分支或转为仅告警。

## 5. 风险与回滚

| 风险 | 等级 | 缓解 |
|------|------|------|
| **InMemory/EF/Redis 序列化语义不一致**（属性命名、嵌套形态）导致缓存命中与 DB 直读形状不同 | P0 | 8a 先落三存储 round-trip 契约测试；EF 写侧统一 Web options；Redis 不改命名策略的前提下以测试锁定 |
| 旧消息（PascalCase dict）读取回归：Files 内层 JSON 再解析失败 | P0 | 转换器逐键防御式解析；解析失败进 Extensions 并告警（不再静默 null）；用生产样本形态写测试（含 `d449647` 前的历史） |
| 8b/8c 发布顺序：8b 写新形态后、8c 未上线前，旧前端读 `metadata?.Files`（PascalCase 键）取不到 | 高 | 方案一：8b 与 8c 同窗发布；方案二：8b 的 API 出站投影临时同时带 `Files`（旧）与 `files`（新）双键，8c 上线后移除 |
| `ContextSummary.compactedMessages`（`types.ts:121`）等嵌套消息列表随契约变化 | 中 | 归属 8a 的契约测试覆盖 ContextSummary 整体序列化 |
| 第三方直接消费 `/conversations` API 者依赖 dict 形态 | 中 | API 是平台内部前端专用 + 版本仍在 0.x；在 8b 发布说明中声明破坏性变化，Extensions 兜底未知键 |
| EF `JsonException` 静默吞掉的历史行为被改为告警，日志量上升 | 低 | 采样告警；仅首次命中打 Warning |

回滚：8a/8b 各自可独立 revert（读侧始终双形态，revert 写侧即回到旧形态写入，数据无损）；
8c revert 后前端回到 JSON.parse 兼容路径，同样可用。

## 6. 验收标准

1. `Backend/src` 与 `Frontend/OpenAgent.Chat/src` 中 `grep -rn "\"Files\"\|'Files'"` 无
   元数据键残留（S3 对象键等非元数据用途除外）。
2. `api.ts` 不再出现对 metadata 值的 `JSON.parse`（附件清单解析后移）。
3. 三存储一致性测试矩阵全绿：同一 `ConversationMessage`（含 Files/Reasoning/
   ExecutionStatus/ToolArguments/未知键）经 EF jsonb 写读、Redis 序列化 round-trip、
   InMemory 直存后，`ConversationMessageMetadata` 深相等（含 Extensions 保真）。
4. 旧形态样本（PascalCase dict、内层 JSON 双命名）读取测试通过；解析失败路径产出
   Extensions + 告警，不再返回 null。
5. E2E：上传文件对话 → 刷新会话 → 附件卡片、中止轮次的"响应已取消/失败"提示
   （`ChatMessages.vue:104-105`）、工具参数展示（`messagePresentation.ts:142`）行为与改造前一致。
6. `docs/database/tables/ConversationMessages.md` 更新双形态说明与终态示例。
