# 会话存储

会话存储链路负责 Agent 执行侧的消息持久化与读取，确保多轮对话的上下文在请求之间完整保留。

## 核心能力

- 按 `conversationId + tenantId` 读取/创建会话及历史消息
- 追加本轮消息（user / assistant / tool）
- 更新会话状态（Running / Completed / Failed / Cancelled）
- 乐观并发控制，版本冲突时自动重试

## 存储分层

```
IConversationStore (热存储)
  ├─ InMemoryConversationStore      ← 开发/测试，无 Redis 时回退
  ├─ RedisConversationStore         ← 生产，仅热存储
  └─ DualWriteConversationStore     ← 生产，热 + 冷双写
          └─ IConversationRepository (冷存储)
                ├─ SqlServerConversationRepository
                └─ SqliteConversationRepository
```

写入路径：热存储（Redis）同步成功即返回，冷归档异步补偿。

## 当前状态

**已实现** — InMemory / Redis / DualWrite 三级存储链路均已落地。

## 当前限制

- 仅执行侧读写，无查询侧 API
- 无展示态权限控制
- 无独立幂等键去重链路

## 规划中

- 软删除与审计保留
- 会话标题生成（截取 + LLM 摘要）
- 数据分层管理（90 天活跃 + 归档表）

## 源码位置

- 接口：`Backend/src/OpenAgent.Contracts/Conversation/IConversationStore.cs`
- 实现：`Backend/src/OpenAgent.Core/Conversation/Store/`
- 冷归档：`Backend/src/OpenAgent.Core/Conversation/Repository/`
