# Agent.Hosting 详细设计文档

## 1. 架构概览
`Agent.Hosting` 是 ASP.NET Core 的宿主封装层。它负责将 `Agent.Core` 的纯逻辑通过 HTTP/gRPC 端点暴露出去，处理请求管线、全局异常、CORS、Swagger 生成以及依赖注入（DI）。

## 2. 核心组件与管线设计

### 2.1 AgentHostingExtensions - DI 注册聚合
提供聚合的扩展方法，屏蔽底层的复杂注册逻辑：
- **AddAgentHost**: 注册 CORS、Swagger、Controllers、认证授权、Agent Core 服务
- **UseAgentHost**: 配置 HTTP 管线中间件、Swagger UI、路由映射

### 2.2 GlobalExceptionHandlerMiddleware - 全局异常处理
统一捕获并格式化异常为 RFC 7807 Problem Details。

异常映射规则：
| 异常类型 | HTTP 状态码 | Problem Details `type` |
|----------|-------------|------------------------|
| `UnauthorizedAccessException` | 403 Forbidden | `https://error.agent.com/unauthorized` |
| `AgentException` 子类 | 400/403/409 等 | `https://error.agent.com/{ErrorCode}` |
| `HumanApprovalRequiredException` | 202 Accepted | `https://error.agent.com/approval-required` |
| `TimeoutException` | 504 Gateway Timeout | `https://error.agent.com/timeout` |
| 其他未处理异常 | 500 Internal Server Error | `https://error.agent.com/internal-error` |

### 2.3 SSE 错误熔断机制
在 SSE 流式输出场景下，异常处理需要特殊处理：
1. 发送终端事件 `event: error\ndata: {...error json...}\n\n`
2. 随后发送 `event: done\ndata: [DONE]\n\n`
3. 优雅关闭响应流，防止前端无限等待

### 2.4 OpenTelemetryMiddleware - 可观测性集成
- 提取或生成 `TraceId`（支持 `X-Correlation-Id` 透传）
- 注入到 `Activity.Current`，实现分布式追踪
- 记录 HTTP 请求级别的 Metrics（QPS, Latency, Error Rate）
- 收集 Controller/Minimal API 级别的日志

### 2.5 CORS 配置策略
支持灵活且安全的跨域策略配置，允许前端工程安全调用 Agent 接口

## 3. 端点设计 (Endpoints)

### 3.1 REST API 端点
| 方法 | 路径 | 模式 | 说明 |
|------|------|------|------|
| POST | `/api/v1/agent/chat` | Blocking | 传统短连接，返回完整 JSON |
| POST | `/api/v1/agent/chat/stream` | NDJSON Streaming | 返回 `IAsyncEnumerable` |
| GET | `/api/v1/agent/chat/sse` | SSE | Server-Sent Events 逐字输出 |
| GET | `/health` | - | K8s 健康检查探针 |
| GET | `/ready` | - | K8s 就绪检查探针 |

### 3.2 CancellationToken 穿透
客户端断开连接时，`HttpContext.RequestAborted` 被触发，Token 被 Cancel，底层 LLM 推理和 MCP 调用立即中止

## 4. Swagger / OpenAPI 配置

### 4.1 多版本 API 支持
支持 API 版本控制（如 `/v1/agent/chat`），在 Swagger UI 中提供版本切换

### 4.2 安全定义
Swagger UI 必须配置 OAuth2 / Bearer Token 鉴权输入框，便于开发与测试人员直接在页面发起受保护的调用

### 4.3 XML 注释集成
支持从 XML 注释文件生成 API 文档

## 5. 连接与生命周期管理

### 5.1 Mock / TestHost 支持
提供沙盒降级配置（如 `AddMockServices()`, `UseMockPermissionEvaluator()`），允许在 `Agent.TestHost` 中跳过复杂的 SSO 验证，方便本地开发联调

### 5.2 健康检查集成
提供 `/health` 和 `/ready` 端点，供 K8s 或外部监控探针探测服务存活和就绪状态

### 5.3 优雅停机 (Graceful Shutdown)
在容器化部署中，当节点被缩容或重启时，等待正在执行的 Skill/LLM 请求处理完毕后再终止进程
