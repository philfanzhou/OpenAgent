# 会话架构重构（2026-09）

单一事实源：2026-09 会话架构审查及其分波次重构的背景、进度与后续路线。
**文档政策**：已合并的 PR 与已完成的重构不保留单独报告——PR 描述即报告；本文件只维护
索引级状态。基线审查结论：功能验证通过（构建 0 错误、638 测试全绿 @9b694ab），架构骨架
健康（MAF-first、PostgreSQL 唯一事实源、写穿透缓存、乐观并发），债务集中在五处结构性问题
（DTO 镜像链、上帝适配器、胖接口、配置上帝对象、横切穿参），典型字段级变更穿透 4~6 层
（极端案例 `d449647`：41 文件）。

## 第一波：已完成（PR #110~#115，全部合并）

| # | 主题 | 核心变更 | 效果 |
|---|------|----------|------|
| 110 | 统一 ChatRequest.Context 解析 | 共享 `ChatRequestContext`（Hosting，架构测试限定 Router 只能引用 Contracts+Hosting）；Engine 采纳忽略大小写+严格值语义 | 修复大小写变体键导致 Router 路由/Engine 生成新会话的分裂隐患 |
| 111 | bwrap 沙箱参数单一事实源 | 60 行逐字重复的安全加固参数收敛为段方法，两变体只保留真差异；前后参数逐项 diff 零差异 | 安全参数改动不再双写（漏写=沙箱逃逸面） |
| 112 | 移除 ArchivedAt（ADR-0004） | 删除"创建即归档"的死字段 | 消除误导性预留语义；未来归档按 ADR 约束重新设计 |
| 113 | 会话 DTO record 化 | ConversationMessage/Record → record；5 处手写整对象复制改 `with`；重试保留 MessageId/IdempotencyKey | 加字段不再逐复制点同步；修复重试丢幂等键缺陷 |
| 114 | IConversationStore 拆 Reader/Writer | 9 方法胖接口 → 5+5 窄契约 + 组合接口；纯读消费方窄化；DI 同实例转发 | 能力隔离；对齐 MAF AgentSessionStore 窄面设计 |
| 115 | PlatformChatHistory 拆分 | 676 行上帝适配器 → 377 行编排器 + HistoryRepair（纯函数）/StreamingTurnBuffer/ConversationTurnLock/HistoryFileInflater | 五类职责单一化；调用方与既有测试零改动 |

评审准则：除测试外代码净缩减。第一波终态：#110 +6、#111 +2、#112 −6、#113 −63、
#114 +16、#115 +84（后两者为接口/文件拆分的结构性脚手架，逻辑零新增，已在各 PR 描述核算）。

## 第二波：已提交评审（#118→#120、#119→#121 两条堆叠链）

| PR | 主题 | 方案要点 | 状态 |
|----|------|----------|------|
| PR-7（#118） | TurnContext 统一轮次上下文 | Contracts 新增 `TurnContext`（Tenant/User/Conversation/Trace/Agent 一次构造），AgentExecutor 唯一构造点；AgentFactory/ConversationHistoryFactory/FileAssetExecutionContext 消费派生投影（ToFileAssetScope/ToCapture/ToConversationContext）；删除 AgentFactory 内重复 traceId 兜底（主链路死代码）与双份 tenant 归一化。**明确不用 AsyncLocal**（工具并行化后语义风险）；FileAssetExecutionContext 保持 Scoped ambient。新横切字段 = 只改 TurnContext 一处 | 待评审 |
| PR-8（#119） | 会话消息 Metadata 类型化 | `ConversationMessageMetadata`（Files 强类型/Reasoning/ExecutionStatus/ToolArguments/Extensions 逃生舱）取代 string-dict；EF jsonb 双形态读取（旧 PascalCase dict 兼容、解析失败降级 Extensions+告警而非静默 null）、写统一 camelCase（EF/Redis 同语义）；前端删除 JSON.parse 与双命名兼容。**P0 门禁：EF/Redis/InMemory 三存储 round-trip 深相等测试矩阵** | 待评审 |
| PR-9（#120，堆叠于 #118） | AgentConfig 依赖拆分（CapabilityContext） | 债务实测：`ICapabilitySource` 整只传 AgentConfig，而 UserProfile/FileAsset 两个 source 根本不用 config，Mcp/ContextPolicy 已窄化。引入 `CapabilityContext`（AgentId/TenantId + Mcp/Rag/Skills/CodeExecution 节）；无依赖参数删除；EF 已按关注点分列（5 jsonb）→ **零 migration**。9c 文件拆分（LlmConfig 等移出 AgentConfig.cs）可选 | 待评审 |
| PR-10（#121，堆叠于 #119） | 前端契约类型手工对齐 | 原规划的代码生成方案（工具导出+CI 门禁）经评审否决——前端类型无需自动化生成。重写为手工清偿既有漂移：补齐 `ConversationMessage.traceId/idempotencyKey/fileIds`、`ConversationRecord.type/version/isDeletedByUser/deletedAt/traceId`、`FileAsset.objectKey` 必填化，修 3 处消费端 | 待评审 |

## 约定

- 第二波起的新横切字段（租户配额、审计开关等）一律走 TurnContext，不逐层穿参（兑现
  ADR-0003 的"内部调用元数据"待办）。
- 前端契约类型手工维护于 `types.ts`（自动化生成方案经评审否决）；改 Contracts 时同步镜像，依赖 review 约定。
- 已完成 PR 的详细动机/改动清单/验证证据见对应 PR 描述（#110~#115），不再另存文档。
