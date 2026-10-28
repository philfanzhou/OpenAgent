# HTTP API 规范

本文是全平台 HTTP API 的唯一规范：所有新端点必须遵循。历史决策见
[ADR-0004](../decisions/0004-Unified-API-Style-Minimal-Api-ProblemDetails.md)。

## 总则

| 项 | 约定 |
|----|------|
| 风格 | Minimal API + `TypedResults`，`MapGroup` + `MapXxx` 扩展方法组织；不新增 Controller |
| 版本前缀 | `/api/v1/...`（所有业务端点，含 Runner） |
| 成功响应 | 直接返回业务 DTO（camelCase JSON），不做 `{code,message,data}` 信封 |
| 错误响应 | 统一 RFC 7807 ProblemDetails（见下），`application/problem+json` |
| 文档 | 每个端点建议 `.WithName`（operationId，全服务唯一）+ `.WithTags`（分组）；不要求更多元数据 |
| Swagger | 仅 Development 环境开启；响应 schema 由 TypedResults 返回类型自动推导，零端点侵入 |
| 认证 | `Authorization` 头：生产 JWT Bearer 或 Bearer API Key；开发环境另有 Basic |

## 成功响应

单一结果直接返回 DTO（自动 200）；多结果用联合类型，Swagger 据此推导响应 schema：

```csharp
// 单一结果：Task<ChatResponse>
private static async Task<ChatResponse> ExecuteAsync(...) => new ChatResponse { ... };

// 多结果：Results<Ok<T>, NotFound, ForbidHttpResult>
private static async Task<Results<Ok<ConversationRecord>, NotFound, ForbidHttpResult>> GetAsync(...)
```

约束：

- 响应 DTO 一律用具名类型（`OpenAgent.Contracts/Responses/` 或领域 DTO），禁止匿名对象。
- 二进制内容用 `TypedResults.File(...)`；创建成功返回 `TypedResults.Created(uri, dto)`。
- 204 无体的删除/撤销返回 `TypedResults.NoContent()`。

## 错误响应（ProblemDetails）

所有非 2xx JSON 错误的统一形态：

```json
{
  "type": "https://error.agent.com/not-found",
  "title": "NotFound",
  "status": 404,
  "detail": "面向用户的可读信息",
  "instance": "/api/v1/agent/files/xxx",
  "traceId": "00-0767a68...-01",
  "timestamp": "2026-09-21T02:00:12Z",
  "errorCode": 8004
}
```

| 字段 | 说明 |
|------|------|
| `type` | `https://error.agent.com/{符号名}`；符号名为 kebab-case（`AgentErrorCode` 枚举名或 Router 路由错误名） |
| `traceId` | 统一解析顺序：`X-Trace-Id` 头 → `Activity.Current.Id` → `TraceIdentifier` |
| `errorCode` | `AgentErrorCode` 整数（`OpenAgent.Contracts/Requests/AgentErrorCode.cs`），唯一错误码体系；Router 路由错误无对应整数时不携带此字段 |

实现方式（按场景选择，禁止手拼 JSON）：

| 场景 | 用法 |
|------|------|
| 端点内直接返回错误 | `TypedResults.Problem(AgentProblemDetails.Invalid("keyword is required", context))`（`Hosting/Errors`） |
| 领域错误 | 抛 `AgentException(AgentErrorCode.X, message)`，由全局中间件转换 |
| 中间件内（无 IResult） | `AgentProblemDetails.WriteAsync(context, problem)` |
| Router 路由错误 | `TypedResults.Problem(RouterProblem.From(exception, context))` |
| Runner（轻依赖） | 本地 `RunnerProblem.Create` 复刻同一契约 |

错误码段位：1xxx Skill / 2xxx MCP / 3xxx RAG / 4xxx LLM / 5xxx Tenant / 6xxx Audience /
7xxx HumanApproval / 8xxx 请求与通用（含 `NotFound=8004`、`AuthenticationRequired=8005`、
`RateLimited=8006`）/ 9xxx 内部与依赖。Router 路由层特有错误用字符串符号名
（`agent-not-found` 等，`RouterErrorCodes`），不强行映射整数段。

保留的空体错误（防探测语义，勿改）：分享下载 404、会话/文件归属探测 404/403 中的
`NotFound()`/`Forbid()`。

## SSE 流式契约

- 事件名：`conversation` / `reasoning` / `tool_call` / `tool_result` / `content` / `done`；心跳为注释行。
- 错误：`event: error`，data 为 camelCase `{type,title,detail,traceId}`；随后
  `event: done`，data 为 `{"done":true,"status":"error"}`。
- Router 转发失败时的降级同样遵循上述 SSE 形态；NDJSON 流保持
  `{"type":"error","error":{title,detail,traceId}}` 结构。

## 路由与命名

- 资源复数名词：`/api/v1/agent/conversations/{conversationId}`；子动作用 POST 子路径
  （`/compact`）。
- 字面路由必须先于参数路由注册（如 `/files/object` 先于 `/files/{fileId}`）。
- operationId 全服务唯一；Router 端点带 `Router` 前缀避免与 Engine 文档冲突。
- 遗留别名 `/api/v1/agents` 仅为兼容保留，已 `ExcludeFromDescription()`，禁止新增别名。

## 各服务的 Swagger

| 服务 | 地址（开发环境） | 说明 |
|------|------------------|------|
| Engine.Host | `/swagger` | 主 API 文档（含 dev-only 管理端点） |
| Router | `/swagger` | 网关本地端点 + 转发端点 |
| Runner | `/swagger` | 沙箱执行 sidecar（内联配置，不依赖 Hosting） |

响应 schema 由端点的 TypedResults 返回类型自动推导，端点代码不含任何 Swagger 专属元数据
（`WithName`/`WithTags` 除外，它们同时服务于路由标识与分组）。认证统一用 Bearer 安全定义
（Authorize 按钮可用）。
