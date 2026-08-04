# Key Flows — Agent.Core

## Flow 1: 非流式推理

触发条件：上层服务调用 `IAgentPipeline.ExecuteAsync`

```
Caller          Pipeline              Service             Engine           Tool/Skill/MCP
  │                │                     │                   │                  │
  │──ExecuteAsync──▶│                     │                   │                  │
  │                │──Middleware Chain──▶ │                   │                  │
  │                │  (AgentIdValidation) │                   │                  │
  │                │  (TenantValidation)  │                   │                  │
  │                │  (Tracing)           │                   │                  │
  │                │  (Auth)              │                   │                  │
  │                │  (AuditLogging)      │                   │                  │
  │                │                     │──AcquireLock─────▶│ (Redis)          │
  │                │                     │                   │                  │
  │                │                     │──ResolveConfig──▶ │                  │
  │                │                     │──CreateEngine───▶ │                  │
  │                │                     │──LoadSkills─────▶ │                  │
  │                │                     │──LoadMcpTools───▶ │                  │
  │                │                     │──ChatCompletion──▶│                  │
  │                │                     │                   │──ToolCall?──────▶│
  │                │                     │◀──ToolResult──────│◀─────────────────│
  │                │                     │──ChatCompletion──▶│  (循环最多N轮)   │
  │                │                     │◀──FinalResult─────│                  │
  │                │                     │──SaveConversation─│                  │
  │                │                     │──ReleaseLock─────▶│ (Redis)          │
  │◀─AgentResponse─│◀────────────────────│                   │                  │
```

关键数据：
- 默认最大轮次：5（可通过 AgentConfig.MaxTurns 配置）
- 工具调用路由：`search_knowledge_base` → RAG，`mcp_*` → MCP，其余 → Skill
- 会话保存：每轮结束后追加消息，状态标记为 Running/Failed/Cancelled
- 分布式锁：以 `lock:conversation:{tenantId}:{conversationId}` 为 key，保证同一会话串行执行

## Flow 2: 流式推理

触发条件：上层服务调用 `IAgentPipeline.ExecuteStreamAsync`

```
Caller          Pipeline              Service             Engine
  │                │                     │                   │
  │──ExecuteStream─▶│                     │                   │
  │                │──Middleware Chain──▶ │                   │
  │                │                     │──AcquireLock─────▶│ (Redis)
  │                │                     │──StreamingChat───▶│
  │◀─chunk─────────│◀─chunk──────────────│◀─chunk────────────│
  │◀─chunk─────────│◀─chunk──────────────│◀─chunk────────────│
  │◀─[Calling tool]│◀─toolCall───────────│                   │
  │                │                     │──ExecuteTool──────▶
  │◀─chunk─────────│◀─chunk──────────────│◀─chunk────────────│
  │◀─done──────────│◀────────────────────│◀──────────────────│
  │                │                     │──ReleaseLock─────▶│ (Redis)
```

差异点：
- 流式场景下，工具调用结果以 `[Calling tool: xxx]` 形式混入流中
- 异常/取消时，先持久化已产生的部分消息，再抛出异常
- 流式场景锁持有时间更长，锁超时需大于 ActivityTimeout

## Flow 3: 会话存储双写

触发条件：Service 执行完成后调用 `SaveConversationAsync`

```
Service           DualWriteStore        RedisStore          SqlServerArchive
  │                    │                     │                     │
  │──AppendMessages───▶│                     │                     │
  │                    │──AppendMessages───▶ │                     │
  │                    │◀──AppendResult─────│                     │
  │                    │──ArchiveAsync──────│────────────────────▶│
  │                    │  (fire-and-forget) │                     │
  │◀──AppendResult────│                     │                     │
```

关键设计：
- 热存储（Redis）写入成功即返回，冷归档异步执行
- 冷归档失败不影响热存储一致性
- 版本冲突时自动重试一次（重新加载版本号再追加）

## Flow 4: 会话一致性保证（分布式锁）

触发条件：同一 conversationId 的并发请求到达不同 Engine 实例

### 问题场景

在 Engine 分布式部署下，同一会话的请求可能被路由到不同 Engine：

```
Client           Router            Engine-A           Engine-B          Redis
  │                │                   │                   │               │
  │──req1(conv:1)──▶│──forward────────▶│                   │               │
  │──req2(conv:1)──▶│──forward────────────────────────────▶│               │
  │                │                   │                   │               │
  │                │                   │──GetRecord(v5)───▶│               │
  │                │                   │◀──record(v5)──────│               │
  │                │                   │                   │──GetRecord(v5)▶│
  │                │                   │                   │◀──record(v5)──│
  │                │                   │                   │               │
  │                │                   │  [both推理基于v5] │               │
  │                │                   │                   │               │
  │                │                   │──Append(v5)──────▶│               │
  │                │                   │◀──ok(v6)──────────│               │
  │                │                   │                   │──Append(v5)──▶│
  │                │                   │                   │◀──conflict!──│
  │                │                   │                   │               │
  │                │                   │  [推理结果基于旧上下文，可能错误]   │
```

### 解决方案：Core 分布式锁 + Router 会话亲和

**Core 层（硬保证）**：同一 conversationId 的推理必须串行执行

```
Engine-A          Redis             Engine-B
  │                 │                   │
  │──SET NX EX─────▶│                   │
  │  lock:conv:1    │                   │
  │◀──ok (acquired) │                   │
  │                 │                   │──SET NX EX─────▶│
  │                 │                   │  lock:conv:1    │
  │                 │                   │◀──conflict      │
  │                 │                   │  (wait/retry)   │
  │  [execute]      │                   │                  │
  │──DEL───────────▶│                   │                  │
  │  lock:conv:1    │                   │                  │
  │                 │                   │──SET NX EX─────▶│
  │                 │                   │  lock:conv:1    │
  │                 │                   │◀──ok (acquired) │
  │                 │                   │  [execute]      │
```

**Router 层（性能优化）**：同一 conversationId 尽量路由到同一 Engine

```
Client           Router                      Engine-A    Engine-B
  │                │                              │          │
  │──req1(conv:1)──▶│──affinity hash─────────────▶│          │
  │──req2(conv:1)──▶│──affinity hash─────────────▶│          │
  │                │  (same conv → same engine)   │          │
  │                │                              │          │
  │  [减少锁争用，提高本地缓存命中]                  │          │
```

### 一致性保障层次

| 层次 | 机制 | 保证 | 位置 |
|------|------|------|------|
| 1. 分布式锁 | `lock:conversation:{tenantId}:{conversationId}` SET NX EX | 同一会话串行执行，推理基于最新上下文 | Core (Service) |
| 2. 乐观并发控制 | `expectedVersion` 检查 | 写入不覆盖，冲突检测 | Core (RedisConversationStore) |
| 3. 会话亲和路由 | conversationId 一致性哈希 | 减少锁争用，提高缓存命中 | Router (IRouteTable) |

### 锁设计要点

- **锁 key**：`lock:conversation:{tenantId}:{conversationId}`
- **锁超时**：非流式 30s，流式场景需大于 ActivityTimeout（当前 100s）
- **获取失败策略**：等待重试（可配置最大等待时间和重试间隔）
- **异常安全**：无论执行成功或失败，必须在 finally 中释放锁
- **Engine 宕机**：锁有 TTL 自动过期，不会死锁
- **与版本控制互补**：锁防止并发执行，版本号防止并发写入，两者缺一不可
