# Conversation Lock — 分布式会话锁

> 本文档描述 Agent.Core 的分布式会话锁机制，用于在多 Engine 部署场景下保证同一会话的串行执行。

## FEATURE

### 核心用户故事

作为 Agent 系统，我希望在同一 `{tenantId, conversationId}` 维度上对会话执行进行串行化，以便在多 Engine 实例部署时避免并发推理导致上下文不一致。

### 功能名称和一句话概括

分布式会话锁 — 基于 Redis SET NX EX + Lua 脚本 + 心跳续期的会话级互斥锁。

### 补充约束

- 锁的粒度为 `{tenantId, conversationId}`，不同会话互不干扰
- TTL 默认 30 秒，可通过配置覆盖
- 心跳频率 = `TTL / 3`，确保长 LLM 调用期间锁不过期
- Owner token 防止误释放（只允许持有者释放）
- 与 `IConversationStore` 的乐观并发控制互补：锁保证执行串行，乐观锁保证写入不冲突

### 范围外

- 会话存储的乐观并发控制（见 `conversation/store/`）
- Router 的会话亲和路由（见 `Agent.Router/docs/`）
- 跨租户的锁隔离（已通过 key 设计天然隔离）

---

## SPEC

### 锁语义

- 同一 `{tenantId, conversationId}` 串行执行；不同组合互不干扰
- TTL 默认 30 秒，可通过 `AgentConfig.LlmSettings.ConversationLockTtlSeconds` 覆盖
- Heartbeat 频率 = `ttl / 3`
- 获取失败的 HTTP 状态码：409 Conflict（`AgentErrorCode.Conflict`）
- 锁 key 格式：`lock:conversation:{tenantId}:{conversationId}`
- Owner token：UUID（`Guid.NewGuid().ToString("N")`，无连字符），用于防止误释放
- 锁释放的原子性保证：Lua 脚本 GET + DEL
- 锁续期的原子性保证：Lua 脚本 GET + PEXPIRE
- 与现有 `IConversationStore` 乐观锁的关系：互补，不互斥

### 接口契约

```csharp
public interface IConversationLock
{
    Task<IConversationLockHandle?> TryAcquireAsync(
        string tenantId,
        string conversationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}

public interface IConversationLockHandle : IAsyncDisposable
{
    string TenantId { get; }
    string ConversationId { get; }
    string OwnerToken { get; }
    bool IsHeld { get; }
}
```

### 错误码

| 错误码 | 含义 | HTTP 状态 |
|--------|------|-----------|
| `AgentErrorCode.Conflict` (8101) | 会话正在被其他请求处理 | 409 |

### 会话亲和哈希 Key 格式

| 用途 | 格式 | 存储 |
|------|------|------|
| **Lock Key**（Redis 实际存储） | `lock:conversation:{tenantId}:{conversationId}` | 存在 Redis |
| **Hash Key**（路由决策用，不存储） | `{tenantId}:{conversationId}` | 不存储 |

**设计要点**：
- Hash key 必须包含 tenantId，防止跨租户的 conversationId 碰撞
- 同一 `Lock Key` 和 `Hash Key` 的核心部分都包含 `{tenantId}:{conversationId}`，但用途不同
- Lock key 由 Core 层管理（写入 Redis），Hash key 仅用于 Router 选 engine
- tenantId 缺失时降级为 `conversationId`（向后兼容）

---

## DESIGN

### 实现组件

| 组件 | 位置 | 职责 |
|------|------|------|
| `IConversationLock` | `Backend/src/OpenAgent.Contracts/Conversation/IConversationLock.cs` | 锁抽象接口 |
| `RedisConversationLock` | `Backend/src/OpenAgent.Core/Conversation/Lock/RedisConversationLock.cs` | Redis 实现（生产） |
| `InMemoryConversationLock` | `Backend/src/OpenAgent.Core/Conversation/Lock/InMemoryConversationLock.cs` | 内存实现（单机/测试） |
| `AgentExecutor` | `Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs` | turn 边界获取/释放锁 |

### Lua 脚本

**release-lock.lua**（原子释放）：

```lua
local current = redis.call('GET', KEYS[1])
if current == ARGV[1] then
    return redis.call('DEL', KEYS[1])
else
    return 0
end
```

**extend-lock.lua**（原子续期）：

```lua
local current = redis.call('GET', KEYS[1])
if current == ARGV[1] then
    return redis.call('PEXPIRE', KEYS[1], ARGV[2])
else
    return 0
end
```

### Owner Token

- 生成：`Guid.NewGuid().ToString("N")`（32 位无连字符）
- 用途：防止误释放（只允许持有者释放）
- 存储：作为 Redis value 存储在 lock key 中

### Heartbeat 实现

- `RedisConversationLockHandle` 内部启动后台 `Task`
- 间隔：`TTL / 3`（默认 10 秒）
- 操作：调用 `extend-lock.lua` 续期
- 异常隔离：心跳失败不传播到主流程，仅记录 Warning 日志
- 取消：`DisposeAsync` 时通过 `CancellationTokenSource.Cancel()` 停止心跳

### Dispose 流程

1. `Interlocked.Exchange` 保证幂等（多次调用安全）
2. 取消心跳 `CancellationToken`
3. 等待心跳 `Task` 完成（吞掉异常）
4. 调用 `release-lock.lua` 释放锁
5. 释放失败仅记录日志，不抛异常

### InMemory 实现

- 使用 `ConcurrentDictionary<string, SemaphoreSlim>` 作为退化方案
- 适用于单机部署或单元测试场景
- 不支持跨进程互斥

### 锁获取失败处理

- `TryAcquireAsync` 返回 `null` 表示获取失败
- `ConversationPreparation` 检测到 `null` 后抛 `AgentException(AgentErrorCode.Conflict)`
- Router/Engine 转换为 HTTP 409 响应
- 客户端可重试

### 时序图

```
Client → Router → Engine                          Redis
  │       │       │                                │
  │ POST /chat     │                                │
  │───────────────>│                                │
  │       │ extract conversationId                 │
  │       │ JumpHash → engine-A                    │
  │       │ forward                                │
  │       │─────────────────────>                   │
  │       │       │ SET NX EX lock:conv:1:abc      │
  │       │       │───────────────────────────────>│
  │       │       │ OK (token=xyz)                 │
  │       │       │<───────────────────────────────│
  │       │       │ Execute core logic             │
  │       │       │  ├─ LLM call (long)            │
  │       │       │  │  heartbeat t=10s:            │
  │       │       │  │  PEXPIRE token=xyz           │
  │       │       │  │────────────────────────────>│
  │       │       │  │ OK                          │
  │       │       │  │<────────────────────────────│
  │       │       │  └─ return response            │
  │       │       │ DEL token=xyz (release)        │
  │       │       │───────────────────────────────>│
  │       │       │ OK                             │
  │       │       │<───────────────────────────────│
  │       │       │                                │
  │<──────────────│                                │
```

### DI 注册

```csharp
// Backend/src/OpenAgent.Core/Exten/CoreServiceExtensions.cs
if (redis configured)
    services.AddSingleton<IConversationLock, RedisConversationLock>();
else
    services.AddSingleton<IConversationLock, InMemoryConversationLock>();
```

---

## TASKS

- [x] `IConversationLock` 接口定义（`Backend/src/OpenAgent.Contracts/Conversation/`）
- [x] `InMemoryConversationLock` 实现（`Backend/src/OpenAgent.Core/Conversation/Lock/`）
- [x] `AgentExecutor` 获取锁并处理准备失败释放；在非流式和流式 finally 中释放锁
- [x] `RedisConversationLock` 实现 + Lua 脚本（release-lock + extend-lock）
- [x] Heartbeat 后台任务（TTL/3 间隔）
- [x] DI 注册扩展（条件注册 Redis/InMemory）
- [x] `JumpHashConsistentHashRing` 一致性哈希实现（Router 侧）
- [x] Router 路由表接入哈希环（session affinity）
- [x] `JwtUserContextMiddleware` 修复注册（AR-03）
- [x] 单元测试（AgentRunConversationLockTests、JumpHashConsistentHashRingTests、SessionAffinityRouteTableTests）

---

## TESTS

### 测试覆盖矩阵

| 场景 | AgentRunConversationLockTests | JumpHashConsistentHashRingTests | SessionAffinityRouteTableTests |
|------|:---:|:---:|:---:|
| 锁可用时正常执行 | ✅ | - | - |
| 锁不可用时抛 Conflict | ✅ | - | - |
| 异常路径锁被释放 | ✅ | - | - |
| 流式路径加锁 | ✅ | - | - |
| 无 conversationId 不加锁 | ✅ | - | - |
| 空环返回 null | - | ✅ | - |
| 单节点稳定映射 | - | ✅ | - |
| 同 key 一致性 | - | ✅ | - |
| 增加节点 ~1/N 重映射 | - | ✅ | - |
| 分布均匀性 | - | ✅ | - |
| UpdateNodes 幂等 | - | ✅ | - |
| 移除节点重分布 | - | ✅ | - |
| 同 convId 路由同 engine | - | - | ✅ |
| 不同 convId 分布到多 engine | - | - | ✅ |
| 增加 engine 保持大部分映射 | - | - | ✅ |
| 目标 engine 离线降级最低负载 | - | - | ✅ |
| 无 convId 退回最低负载 | - | - | ✅ |
| 空 engine 列表返回 null | - | - | ✅ |
| 不同 convId 可路由不同 engine | - | - | ✅ |
| 同 convId 不同 tenant 路由到不同 engine | - | - | ✅ (新增) |
| tenantId 缺失时降级 | - | - | ✅ (新增) |

### 测试文件位置

- `Backend/tests/OpenAgent.Core.Tests/Conversation/InMemoryConversationLockTests.cs`

---

## CONVENTIONS

### Lock Key 命名

- 格式：`lock:conversation:{tenantId}:{conversationId}`
- 必须使用业务前缀（`lock:conversation:`）以便 SCAN 排查
- 禁止直接用裸 `conversationId` 作为 Redis key

### TTL

- 单位：秒
- 默认值：30
- 心跳间隔：`TTL / 3`（默认 10 秒）

### Owner Token

- 格式：`Guid.NewGuid().ToString("N")`（32 位无连字符）
- 日志中只显示前 8 位（`OwnerToken[..8]`），避免泄露完整 token

### 哈希 Key

- 格式：`{tenantId}:{conversationId}`（Router 侧，用于会话亲和选 engine）
- 必须包含 tenantId，避免不同租户的 conversationId 碰撞
- tenantId 缺失时降级为 `{conversationId}`（向后兼容）

### 错误码

- `AgentErrorCode.Conflict`（8101）→ HTTP 409

### 日志

- 前缀：`[ConversationLock]` 用于 grep
- 心跳正常：不打印（Debug 级别）
- 心跳失败：Warning，含 lock key + owner token 前 8 位
- 释放失败：Warning，含 lock key + owner token 前 8 位

### 与乐观锁的关系

| 层 | 机制 | 职责 |
|----|------|------|
| `IConversationLock` | Redis SET NX EX + Lua | 执行串行化（硬保证） |
| `IConversationStore` | expectedVersion 乐观锁 | 写入冲突检测（数据层） |

两者互补：锁保证同一会话不会并发执行，乐观锁保证写入时版本一致。
