# ADR-0004：统一 API 风格（Minimal API + TypedResults）与 ProblemDetails 错误契约

## 状态

已决策，第一版已实现。

## 背景

引入 Swagger 并统一全部 HTTP API 前，各服务的 API 层存在四类不一致：

1. **风格混用**：全库约 40 个端点几乎全部是 Minimal API（`MapGroup` + 扩展方法），仅存一个
   `ConfigurationController`（且其 action 本身返回 `IResult`）；`AddAgentHost` 还为所有宿主注册了
   没有消费者依赖的 `AddControllers`/`MapControllers`。
2. **响应形态各异**：端点返回匿名对象、裸 DTO、`Results.*`（untyped），约 12 种临时形态；
   Swashbuckle 虽已引用，但 untyped `IResult` 使文档只能显示泛型 schema，且无安全定义、无标题。
3. **三套错误体系**：Engine 用 ProblemDetails（扩展 `errorCode` 整数）；Router 用自己的
   `Results.Problem`（扩展 `code` 字符串，snake_case）且无全局异常处理；Runner 是裸
   `Results.Problem(title)` 与 401 空 body。流式错误载荷也有三种不同 JSON 形态。
4. **路由不齐**：Runner 独用 `/v1/execute`（无 `/api` 前缀）；Engine 的 `/files/object` 字面路由
   注册在 `{fileId}` 之后依赖路由优先级。

前端 `Frontend/OpenAgent.Chat/src/api.ts` 依赖错误体的 `detail/title/traceId`（camelCase）解析，
SSE 解析读取 error 事件的 `detail` 字段——任何统一方案必须保留这些字段。

## 决策

### 1. 统一采用 Minimal API + TypedResults（不引入 Controller）

- 沿用 `MapGroup` + `MapXxx` 扩展方法的组织方式；唯一的 `ConfigurationController` 迁移为
  `Extensions/ConfigurationEndpointExtensions.cs`，随后删除 `Controllers/`、`AddControllers()`、
  `MapControllers()`。
- 端点返回值从 `Results.*` 换成 `TypedResults.*`：单一结果直接返回 DTO（自动 200 + schema 推导），
  多结果用 `Results<Ok<T>, NotFound, ...>` 联合类型——Swagger 由返回类型自动生成响应元数据，
  无需手写 `Produces`。
- 匿名响应对象全部替换为 `OpenAgent.Contracts/Responses/` 下的具名 DTO（`MeResponse`、
  `FileAssetResponse`、`FileShareLinkResponse`、`HealthReportResponse`、`AuthConfigResponse`、
  `TokenResponse` 等）；端点用 `.WithName/.WithTags/.WithSummary` 提供文档元数据。

选择 Minimal API 而非 Controller 的理由：95% 代码已是 Minimal API（反向改写量约 20+ 文件），
TypedResults 已满足 OpenAPI 类型推导需求，团队无需维护两套心智模型。

### 2. 统一响应契约：成功裸 DTO + 错误 ProblemDetails（RFC 7807）

- **成功**：直接返回业务 DTO（camelCase JSON），不做 `{code,message,data}` 全量信封——
  与 SSE 流式、文件二进制下载天然兼容，前端零改动。
- **错误**：所有服务统一返回 `application/problem+json`：

```json
{
  "type": "https://error.agent.com/conversation-not-found",
  "title": "NotFound",
  "status": 404,
  "detail": "...",
  "instance": "/api/v1/agent/conversations/xxx",
  "traceId": "00-...-01",
  "timestamp": "2026-09-21T02:00:12Z",
  "errorCode": 8004,
  "code": "not-found"
}
```

扩展字段约定：`traceId`（X-Trace-Id → Activity → TraceIdentifier 的统一解析顺序）、`timestamp`、
`errorCode`（`AgentErrorCode` 整数，唯一错误码体系）、`code`（kebab-case 可读符号名；Router 的
snake_case 字符串码统一转 kebab-case）。新增通用错误码：`NotFound=8004`、
`AuthenticationRequired=8005`、`RateLimited=8006`。

- **SSE 错误事件**统一为 camelCase `{type,title,detail,traceId}` + `event: done` 的
  `{done:true,status:"error"}`；Router 的三种转发错误形态（JSON fallback / SSE / NDJSON）向其靠拢。
- 连接测试端点（`/llm/test-connection` 等）保留 `200 + {success:false}` 语义——"测试已执行"本身
  不是传输错误。

### 3. 共享基础设施落在 OpenAgent.Hosting.Errors

`ProblemDetailsFactory`、`ErrorMapper`、`AgentExceptionHandlerMiddleware` 从 Engine.Host 的
internal 类提升为 Hosting 的公共设施（`AgentProblemDetails` 静态构造器 + `AgentExceptionMapper`
+ 共享中间件 + `AgentProblemDetailsWriter`）。服务特定行为通过
`AgentExceptionHandlingOptions` 注入：

- Engine 注册 `ClientResultException`（OpenAI/Azure SDK）映射与中文 SSE 错误文案；
- Router/Runner 使用默认行为。Runner 保持轻依赖（仅引用 Contracts），用本地
  `RunnerProblem` 复刻同一 JSON 契约。

Router 同时接入共享全局异常中间件（原先未处理异常返回 Kestrel 默认空 500），
`JwtUserContextMiddleware` 缺租户改为抛 `AgentException(TenantNotFound)`、
`RateLimitingMiddleware` 429 补 ProblemDetails body。

### 4. Swagger 完善

- `AddAgentHost` 中 `AddSwaggerGen` 配置：文档标题 = `{ServiceName} API`、Bearer 安全定义
  （JWT/API Key/Basic 共用 Authorization 头）、引入 Contracts 的 XML 注释作为 schema 描述
  （XML 文档生成仅在 Contracts 开启，CS1591 仅在该项目压制）。
- 端点级描述用 `WithSummary/WithDescription` 元数据，避免全解决方案 XML 注释噪音。
- 新增 `AgentHostOptions.SwaggerExposeInNonDevelopment`（默认 false）供测试/预发开启。
- Runner 内联同构配置（不引入 Hosting）；遗留别名 `/api/v1/agents` 保留但
  `ExcludeFromDescription()` 隐藏出文档。

### 5. 路由整理

Runner `/v1/execute` → `/api/v1/execute`（`RunnerClient` 同步修改）；`/files/object` 字面路由
先于 `{fileId}` 注册。

## 影响

- 前端零改动（`detail/title/traceId` 与 SSE `detail` 全保留；Router 错误向 Engine 形态收敛）。
- `AgentException` 的 ProblemDetails `type` URI 从全小写拼接改为 kebab-case
  （`tenantdataisolationviolation` → `tenant-data-isolation-violation`），并新增 `code` 字段。
- 测试改动：`ConfigurationControllerTests` 重写为端点测试；直接调用 handler 的测试改为解包
  `Results<...>.Result`；中间件测试随迁移移至 `OpenAgent.Hosting.Tests`。

## 源码位置

- 共享错误设施：`Backend/src/OpenAgent.Hosting/Errors/`
- 响应 DTO：`Backend/src/OpenAgent.Contracts/Responses/`
- Engine 端点：`Backend/src/OpenAgent.Engine.Host/Extensions/`
- Router 错误治理：`Backend/src/OpenAgent.Router/Endpoints/`、`Security/`、`Middleware/`
- API 规范：`docs/overview/API.md`
