# Router 下游契约 v1（草案）

下游服务实现本文的端点后即可被 Router 转发。决策背景见 [ADR-0004](../../decisions/0004-Router-Gateway-Repositioning.md)。契约尚未发布，字段可能调整。

所有请求都带 `Authorization`，Router 原样透传，下游必须自行校验并解析用户与租户。路径沿用现有 `/api/v1/agent/*`，前端无需改动。

## 下游必须实现的端点

完整定义见 [`downstream-v1.yaml`](../../../Backend/src/OpenAgent.Router.Contracts/openapi/downstream-v1.yaml)，那是端点与字段的唯一来源；本表只做概览。标记为「已实现」的端点按 `OpenAgent.Engine.Host` 的现有代码整理，「提议」的端点尚无任何下游实现。

| 方法 | 路径 | 用途 | 状态 | 要点 |
|------|------|------|------|------|
| POST | `/api/v1/agent/chat` | 一次对话，返回完整答案 | 已实现 | 请求体 `message`、`fileIds`、`context` |
| POST | `/api/v1/agent/chat/stream` | 一次对话，SSE 流式返回 | 已实现 | 每条 `data` 为 `AgentStreamEvent` |
| POST | `/api/v1/agent/chat/intent` | 意图识别 | 已实现 | Router 用 Provider 配置的服务凭据调用，不带用户上下文 |
| GET | `/api/v1/agent/agents` | Agent 目录 | 已实现 | 下游按调用者过滤；`agentId` 在全部下游间必须唯一 |
| GET | `/api/v1/agent/provider/conversations/{id}` | 会话归属探测 | 已实现 | 只返回 204 或 404，不返回内容；无权、跨租户、已删除统一 404 |
| GET | `/api/v1/agent/conversations` | 会话列表 | 已实现 | `skip`、`take` 分页 |
| GET | `/api/v1/agent/conversations/search` | 会话搜索 | 已实现 | `keyword` 加分页 |
| GET / DELETE | `/api/v1/agent/conversations/{id}` | 读取与删除会话 | 已实现 | |
| POST | `/api/v1/agent/conversations/{id}/compact` | 压缩会话 | 已实现 | |
| GET | `/api/v1/agent/me` | 当前用户 | 已实现 | 返回下游解析出的用户与租户 |
| POST | `/api/v1/agent/files` | 上传文件 | 已实现 | multipart |
| GET | `/api/v1/agent/files/{id}`、`/content`、`/download`、`/files/object` | 文件元数据与内容 | 已实现 | 二进制响应，Router 不缓冲 |
| GET | `/health`、`/ready` | 存活与就绪 | 已实现 | ready 失败的实例被摘除 |
| POST | `/api/v1/agent/runs` | 创建异步任务 | 提议 | 立即返回 `runId` 与 202；支持 `Idempotency-Key`，由下游去重 |
| GET | `/api/v1/agent/runs/{id}` | 查询任务状态 | 提议 | 不属于该下游返回 404，用于亲和确认 |
| GET | `/api/v1/agent/runs/{id}/events` | 任务事件流 | 提议 | SSE；每条事件带 `id`，支持 `Last-Event-ID` 续传 |
| POST | `/api/v1/agent/runs/{id}/input` | 补充输入或审批 | 提议 | 仅在 `waiting_input` 状态有效，否则 409 |
| POST | `/api/v1/agent/runs/{id}/cancel` | 取消任务 | 提议 | 幂等；已结束的任务返回当前状态 |

会话 ID 与 Agent ID 通过 `X-Conversation-Id`、`X-Agent-Id` 请求头传入，也可放在请求体 `context.conversationId`、`context.agentId` 中；缺省时由下游生成会话 ID。

### 与 Provider 的关系

Router 只有一种 Provider 实现 `HttpProvider`，按上表的固定路径调用下游；不再支持按下游配置路径，也不再内置 Gina 等协议翻译。协议不同的第三方需要自己提供标准接口。Provider 配置只有 `Id`、可选的 `BaseUrl` 和 `ServiceHeaders`，见 [Agent Provider 集成](../../integrations/agent-provider.md)。

## Router 附加的请求头

| 请求头 | 含义 |
|--------|------|
| `Authorization` | 原样透传，不改写 |
| `X-Agent-Id` | Router 选定的 Agent；显式指定时与请求一致 |
| `X-Conversation-Id` | 会话 ID，用于下游确认归属 |
| `X-OpenAgent-Contract` | Router 期望的契约主版本，下游不支持时返回 426 |
| `traceparent` | W3C 链路追踪，贯穿 Router 与下游 |

## 注册与发现

Router 提供注册端点，下游启动后注册并周期性续约，续约同时充当心跳。

| 方法 | 路径（Router 提供） | 说明 |
|------|--------------------|------|
| PUT | `/api/v1/router/registrations/{instanceId}` | 注册或续约 |
| DELETE | `/api/v1/router/registrations/{instanceId}` | 优雅下线 |

```json
{
  "providerId": "self-engine",
  "baseUrl": "http://engine-7f3a:5208",
  "contractVersion": "1.0",
  "ttlSeconds": 15,
  "eventRetentionSeconds": 86400
}
```

- 注册信息写入 Redis，键名由 `Router.Contracts` 定义；超过 `ttlSeconds` 未续约视为下线。Redis 是 Router 的内部实现，下游只面对上面的 HTTP 接口。
- 静态地址在配置文件中列出，行为与动态注册一致：同样有健康探测与故障隔离，只是没有心跳。
- 同一 `providerId` 的多个实例按会话 ID 一致性哈希分配，实例失败后隔离一段时间再重试。
- 注册接口的鉴权方式 `[待决策]`，见 ADR-0004 风险 1。

## Run 任务

### 状态

`queued` → `running` → `waiting_input` → `completed` / `failed` / `canceled`。`completed`、`failed`、`canceled` 为终态，结果可重复读取。

### 事件流

```text
id: 17
event: progress
data: {"step":"检索资料","percent":40}

id: 18
event: input_required
data: {"inputId":"in-3","prompt":"是否继续执行扣款？","expiresAt":"2026-10-16T09:30:00Z"}

: ping
```

事件类型首批为 `progress`、`message`、`input_required`、`completed`、`failed`。`: ping` 为每 15 秒一次的注释行，用于保活。事件 `id` 在单个 run 内单调递增。

### 职责划分

| 约定 | 负责方 | 内容 |
|------|--------|------|
| 任务状态与事件 | 下游 | 持久化状态与事件，至少保留其声明的 `eventRetentionSeconds`；Router 不存任何任务数据 |
| 断线续传 | 下游 | 客户端带 `Last-Event-ID` 重连，下游从该事件之后重放；超出保留期返回 410 |
| runId 亲和 | Router | 创建时记录 runId 到 Provider 的映射；缓存丢失时向各下游查询 `GET /runs/{id}` 确认 |
| 创建去重 | 下游 | 按 `Idempotency-Key` 去重；Router 已移除通用幂等，不再兜底 |
| 超时 | Router | 事件流不套用 100 秒活动超时，依赖 15 秒心跳；心跳中断才判定连接失效 |
| 扩缩容与发版 | 两者 | 实例收到终止信号后先关闭事件流，客户端凭 `Last-Event-ID` 重连到任意存活实例；任务本身必须能在实例间接管 |

## 身份与可见性约定

- 下游自己验证 token，Router 不做授权判断。
- Router 读取 token 中的租户仅用于路由分片，该值未经验签，下游不得信任请求头中由 Router 附带的任何身份信息替代自身校验。
- 目录接口 `GET /api/v1/agent/agents` 只返回调用者可见的 Agent。Router 不保存权限数据，也不再过滤。`[待确认]` Engine 目前没有 ACL 实现，需先补上。

## OpenAPI 文件

`Backend/src/OpenAgent.Router.Contracts/openapi/downstream-v1.yaml`。已实现端点的路径与字段来自 `OpenAgent.Engine.Host` 与 `OpenAgent.Contracts` 的现有代码；`x-status: proposed` 的 Run 端点是目标设计。`[待确认]` 尚未有契约测试校验 Engine 的真实响应与该文件一致。
