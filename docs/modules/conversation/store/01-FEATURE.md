# Feature: 会话存储链路

## 用户故事

作为执行内核，我希望会话消息被可靠持久化，以便跨请求保持对话上下文。

## 概述

会话存储链路负责 Agent.Core 执行侧的消息持久化与读取，确保多轮对话的上下文在请求之间完整保留。存储层采用渐进式分层架构，从内存存储到 Redis 热存储再到双写冷归档，按配置自动选择。

## 核心能力

- 按 `conversationId + tenantId` 读取已有会话及历史消息
- 创建新会话主记录
- 追加本轮新增消息（user / assistant / tool）
- 更新会话状态（Running / Completed / Failed / Cancelled）
- 乐观并发控制，版本冲突时自动重试

## 当前状态

**已实现** — InMemory / Redis / DualWrite 三级存储链路均已落地，乐观并发控制已生效。

## 当前限制

- 无查询侧 API（仅执行侧读写）
- 无展示态权限控制
- 无独立幂等键（IdempotencyKey）去重链路
- 无面向管理面的恢复和回放接口
- ConversationStoreOptions 中无 RedisConnectionString 字段（由 ServiceExtensions 从配置单独读取）

## 规划中

- **软删除与审计保留**：用户删除会话仅标记 `IsDeletedByUser`，数据物理保留供审计查询，审计端点独立路由并返回脱敏数据
- **会话标题生成**：首轮消息截取为初始标题（必定成功），异步 LLM 摘要更新（失败告警不阻塞）
- **数据分层管理**：`ConversationMessages` 主表保留 90 天活跃数据，超期迁移到 `ConversationMessagesArchive` 归档表（页压缩），后台定时任务驱动，控制单表数据量
