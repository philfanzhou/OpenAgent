# 关键业务流程

本文档描述 Agent.Engine 的 4 个核心业务流程，使用 ASCII 时序图表示。

---

## 流程 1：同步聊天请求

`POST /api/v1/agent/chat`

```
Client          EndpointExtensions      ShutdownService      ConfigProvider      Agent.Core Pipeline
  |                    |                      |                    |                     |
  |  POST /chat        |                      |                    |                     |
  |------------------->|                      |                    |                     |
  |                    |  RegisterRequest()   |                    |                     |
  |                    |--------------------->|                    |                     |
  |                    |  requestId           |                    |                     |
  |                    |<---------------------|                    |                     |
  |                    |                      |                    |                     |
  |                    |  CreateAgentRequest() |                    |                     |
  |                    |  (from ChatRequest)   |                    |                     |
  |                    |                      |                    |                     |
  |                    |  ExtractUserContext() |                    |                     |
  |                    |  (JWT Claims → IAgentUserContext)          |                     |
  |                    |                      |                    |                     |
  |                    |  GetConfigAsync(agentId)                  |                     |
  |                    |------------------------------------------>|                     |
  |                    |  (1) Snapshot → (2) Redis → (3) Mock     |                     |
  |                    |  AgentConfig          |                    |                     |
  |                    |<------------------------------------------|                     |
  |                    |                      |                    |                     |
  |                    |  ExecuteAsync(agentRequest, userContext)  |                     |
  |                    |------------------------------------------------------------->|
  |                    |                      |                    |  AgentResponse       |
  |                    |<-------------------------------------------------------------|
  |                    |                      |                    |                     |
  |                    |  EnsureSuccessfulResponse()               |                     |
  |                    |  CompleteRequest()   |                    |                     |
  |                    |--------------------->|                    |                     |
  |                    |                      |                    |                     |
  |  200 OK {message}  |                      |                    |                     |
  |<-------------------|                      |                    |                     |
```

**关键点：**
- `RequestScope`（`using var scope`）确保请求在 `ShutdownService` 中注册和完成
- `EnsureSuccessfulResponse()` 在 `response.Success == false` 时抛出 `AgentException`
- `ConfigProvider` 按快照 → Redis → Mock 顺序降级

---

## 流程 2：流式聊天请求

`POST /api/v1/agent/chat/stream` (NDJSON) 或 `/api/v1/agent/chat/sse` (SSE)

### NDJSON 流程

```
Client          EndpointExtensions      Agent.Core Pipeline
  |                    |                      |
  |  POST /chat/stream |                      |
  |------------------->|                      |
  |                    |  RegisterRequest()   |
  |                    |  Set Response:       |
  |                    |    200 OK            |
  |                    |    Content-Type:     |
  |                    |    application/x-ndjson
  |                    |                      |
  |                    |  ExecuteStreamAsync() |
  |                    |--------------------->|
  |                    |                      |
  |                    |  await foreach chunk |
  |                    |<----- chunk 1 -------|  {"type":"content","content":"...","traceId":"..."}
  |  NDJSON line       |                      |
  |<-------------------|                      |
  |                    |<----- chunk 2 -------|
  |  NDJSON line       |                      |
  |<-------------------|                      |
  |                    |      ...             |
  |                    |<----- chunk N -------|
  |  NDJSON line       |                      |
  |<-------------------|                      |
  |                    |                      |
  |                    |  WriteNdjsonEvent: done
  |  {"type":"done","status":"completed","traceId":"..."}
  |<-------------------|                      |
  |                    |  CompleteRequest()   |
```

### SSE 流程

```
Client          EndpointExtensions      Agent.Core Pipeline
  |                    |                      |
  |  POST /chat/sse    |                      |
  |------------------->|                      |
  |                    |  Set Headers:        |
  |                    |    Content-Type: text/event-stream
  |                    |    Cache-Control: no-cache
  |                    |    Connection: keep-alive
  |                    |                      |
  |                    |  ExecuteStreamAsync() |
  |                    |--------------------->|
  |                    |                      |
  |                    |<----- chunk 1 -------|
  |  data: {"content":"..."}                  |
  |<-------------------|                      |
  |                    |<----- chunk 2 -------|
  |  data: {"content":"..."}                  |
  |<-------------------|                      |
  |                    |      ...             |
  |                    |                      |
  |  data: [DONE]      |                      |
  |<-------------------|                      |
```

**错误处理差异：**

| 场景 | NDJSON (`/chat/stream`) | SSE (`/chat/sse`) |
|------|-------------------------|---------------------|
| 正常完成 | `{"type":"done","status":"completed"}` | `data: [DONE]` |
| 取消 | `{"type":"done","status":"cancelled"}` | `event: done\ndata: [CANCELLED]` |
| 异常 | `{"type":"error",...}` + `{"type":"done","status":"error"}` | `event: error\ndata: {...}` + `event: done\ndata: [ERROR]` |
| 未捕获异常（中间件层） | `GlobalExceptionHandlerMiddleware` → ProblemDetails | `SseErrorHandlerMiddleware` → SSE error event |

---

## 流程 3：配置热加载

Redis Pub/Sub → `HotReloadService` → `ConfigSnapshot`

```
Agent.Matrix         Redis              HotReloadService         ConfigSnapshot
    |                  |                      |                       |
    |  SET agent:config:{agentId}             |                       |
    |----------------->|                      |                       |
    |  PUBLISH agent:config:updates           |                       |
    |----------------->|                      |                       |
    |                  |  Pub/Sub message     |                       |
    |                  |--------------------->|                       |
    |                  |                      |                       |
    |                  |                      |  LooksLikeJson?       |
    |                  |                      |  ├─ Yes: Parse ConfigUpdate
    |                  |                      |  └─ No:  ProcessLegacyMessage
    |                  |                      |                       |
    |                  |                      |  [结构化消息路径]      |
    |                  |                      |  Type=FullSync?       |
    |                  |                      |  ├─ Yes: Clear()（清空快照，无需 AgentId）
    |                  |                      |  AgentId 缺失? → 忽略
    |                  |                      |  否则: RefreshFullConfigFromRedis
    |                  |                      |  │   GET agent:config:{agentId}
    |                  |                      |  |-------------------->|
    |                  |                      |  |<--------------------|
    |                  |                      |  命中 → SetFullConfig(agentId, config)
    |                  |                      |  未命中 → Evict(agentId)
    |                  |                      |  |-------------------->|
    |                  |                      |                       |
    |                  |                      |  [遗留消息路径]        |
    |                  |                      |  channel=agent:config:changed
    |                  |                      |  payload=agentId (非 JSON)
    |                  |                      |  → RefreshFullConfigFromRedis
    |                  |                      |  |-------------------->|
    |                  |                      |                       |
    |                  |                      |  [其他遗留通道]        |
    |                  |                      |  skill/llm/rag/engine:config:changed
    |                  |                      |  → 仅记录日志，不修改快照
```

**关键点：**
- `HotReloadService` 同时订阅 6 个通道：1 个当前结构化通道 + 5 个遗留通道
- 结构化消息统一全量刷新（ConfigUpdate / IncrementalUpdate 均从 Redis 加载完整配置）
- FullSync 清空整个快照（支持无 AgentId 广播）
- 遗留消息仅携带 agentId 字符串，触发全量从 Redis 重新加载
- 快照条目带 TTL（`AbsoluteExpirationMinutes`），丢失 pub/sub 消息后自动恢复

---

## 流程 4：Engine 生命周期

启动 → 注册 → 心跳 → 停机 → 注销

```
                    ApplicationStarted        HeartbeatService        RedisRegistry          Redis
                         |                        |                       |                    |
  app.Run()              |                        |                       |                    |
     |                   |                        |                       |                    |
     |  ApplicationStarted event                  |                       |                    |
     |------------------>|                        |                       |                    |
     |                   |  DetectPort()          |                       |                    |
     |                   |  SetHost() / SetPort() |                       |                    |
     |                   |  RegisterAsync()       |                       |                    |
     |                   |----------------------->|  StringSetAsync       |                    |
     |                   |                        |---------------------->|  SET engine:registry:{id} (TTL=30s)
     |                   |                        |                       |------------------->|
     |                   |                        |  IsRegistered=true    |                    |
     |                   |                        |<----------------------|                    |
     |                   |                        |                       |                    |
     |                   |  [循环心跳]             |                       |                    |
     |                   |                        |  HeartbeatAsync()     |                    |
     |                   |                        |---------------------->|  StringSetAsync    |
     |                   |                        |                       |------------------->|
     |                   |                        |     (每 IntervalSeconds=10s)                |
     |                   |                        |       ...             |                    |
     |                   |                        |                       |                    |
  SIGTERM / Ctrl+C       |                        |                       |                    |
     |                   |                        |                       |                    |
     |  ApplicationStopping event                 |                       |                    |
     |--> Program.cs callback                     |                       |                    |
     |                   |                        |                       |                    |
     |  ShutdownService.ShutdownAsync(timeout=30s)|                       |                    |
     |------------------>|                        |                       |                    |
     |  IsShuttingDown=true                       |                       |                    |
     |  等待 in-flight 请求完成                    |                       |                    |
     |                   |                        |                       |                    |
     |  RedisRegistry.DeregisterAsync()           |                       |                    |
     |--------------------------------------------------------->|  KeyDeleteAsync   |
     |                   |                        |                       |------------------->|
     |                   |                        |  DEL engine:registry:{id}                  |
     |                   |                        |  IsRegistered=false    |                    |
     |                   |                        |<----------------------|                    |
     |                   |                        |                       |                    |
     |  进程退出          |                        |                       |                    |
```

**关键点：**
- 注册时机：`ApplicationStarted` 事件触发后，先检测端口再注册
- 心跳间隔：`Heartbeat:IntervalSeconds`（默认 10s），TTL：`Heartbeat:RegistryTtlSeconds`（默认 30s）
- 负载计算：`GetCurrentLoad()` 综合内存压力 (40%)、GC 压力 (30%)、线程池压力 (30%)
- 停机顺序：先等待在飞请求 → 再注销 Redis；注销失败则依赖 TTL 自然过期
- Redis 不可用时：注册/心跳失败不阻断启动，Engine 以孤岛模式运行
