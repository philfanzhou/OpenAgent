# 可观测性

本文是 OpenAgent 当前可观测性能力的唯一事实入口。它描述仓库内已经实现的信号、配置与责任边界；具体排障步骤见 [`../trace-troubleshoot.md`](../trace-troubleshoot.md)。

## 当前实现

| 信号 | 应用侧实现 | 暴露方式 | 当前边界 |
|------|------------|----------|----------|
| 日志 | 业务代码使用 `ILogger<T>`，由 Serilog 接管；通用请求事件由 Hosting middleware 横切采集 | Console/stdout；配置 OTLP 地址后同时导出 OpenTelemetry Logs | 日志存储与查询后端由部署环境决定 |
| Trace | OpenTelemetry ASP.NET Core、HttpClient 与服务自定义 `ActivitySource` | 配置 OTLP 地址后导出 | 仓库不内置 Collector 或 Trace 存储 |
| Metrics | OpenTelemetry ASP.NET Core 指标、Hosting 请求指标与服务自定义 `Meter` | Prometheus `/metrics`；配置 OTLP 地址后同时导出 | 长期存储与看板由部署环境决定 |
| Health | ASP.NET Core Health Checks | `/health`、`/ready`，兼容 `/health/live`、`/health/ready` | `ready` 用于依赖可用性，`health` 用于进程存活 |

应用与基础设施的边界如下：

```text
ILogger<T> -> Serilog -> Console/stdout
                    `-> OpenTelemetry Logs -> OTLP（可选）

ASP.NET Core / HttpClient / ActivitySource -> OpenTelemetry
                                             |-> OTLP（可选）
Meter / ASP.NET Core Metrics -> OpenTelemetry
                                |-> OTLP（可选）
                                `-> /metrics（Prometheus Pull）
```

仓库不绑定 Loki、Tempo、Grafana、Prometheus Server 或 OpenTelemetry Collector。Collector 由部署环境
单独提供；持久化、索引和查询后端也由部署环境选择。

## 服务标识与配置

| 服务 | Serilog `ServiceName` | OpenTelemetry service name | 自定义 source/meter |
|------|-----------------------|----------------------------|---------------------|
| Router | `agent-router` | `agent-router` | `OpenAgent.Router` |
| Engine | `agent-engine` | `agent-engine` | `OpenAgent.Engine` |

基础配置：

```json
{
  "OpenTelemetry": {
    "ServiceName": "agent-router",
    "ServiceVersion": "1.0.0",
    "OtlpEndpoint": "https://otel-collector.intra.example:4317"
  }
}
```

- `OpenTelemetry:OtlpEndpoint` 可省略；省略时不注册 OTLP logs、traces 和 metrics exporter，Console 与 `/metrics` 仍可用。
- 也可以使用标准环境变量 `OTEL_EXPORTER_OTLP_ENDPOINT`。
- 应用 Compose 通过 `OPENAGENT_OTLP_ENDPOINT` 配置外部 Collector，统一汇集 Engine 与 Router 的 Logs、
  Traces 和 Metrics；本项目不创建或管理 Collector 容器。
- OTLP 地址存在但不是绝对 HTTP(S) URI 时启动失败，避免服务看似正常但遥测静默丢失。
- `OpenTelemetry:ServiceName` 与 `ServiceVersion` 可以覆盖应用默认值。
- Serilog 的输出和最小级别由各服务 `appsettings*.json` 中的 `Serilog` 节控制。

## 指标

Router 当前提供以下业务指标：

| 指标 | 标签 | 含义 |
|------|------|------|
| `http.server.request.duration` | OpenTelemetry ASP.NET Core 标准属性 | HTTP 请求耗时；Prometheus histogram 的 count 同时表示请求量 |
| `openagent_router_forwarding_failures_total` | `action`、`forwarder_error` | YARP 转发失败数 |
| `openagent_router_provider_selections_total` | `source` | Provider 选择来源与结果 |
| `openagent_router_acl_denials_total` | `reason` | Agent ACL 拒绝次数 |
| `openagent_router_cache_hits_total` | `cache` | 幂等/查询缓存命中 |
| `openagent_router_rate_limit_decisions_total` | `outcome`、`source`、`degraded` | 限流允许、拒绝与降级决策 |
| `openagent_router_discovery_selections_total` | `intent`、`source` | 动态发现、静态回退或错误选择 |

标签值会被裁剪并转为小写，空值归一为 `unknown`。新增指标必须保持低基数；不能把 `TraceId`、`ConversationId`、`AgentId`、`UserId`、`TenantId`、URL 或异常文本放入指标标签。

## 日志属性与 Trace 标识

`ServiceName`、`ServiceVersion`、`InstanceId` 是所有 Serilog 事件的稳定属性。请求日志按上下文记录 `TraceId`、`ConversationId`、`AgentId`、`UserId`、`TenantId` 等高基数字段，这些字段用于单次请求定位，不应提升为日志存储的索引标签。

`X-Trace-Id` 是 OpenAgent 的请求关联标识；W3C `traceparent` 中的 Activity Trace ID 是分布式追踪标识。两者都应在排障证据中保留，但不能假设它们始终相同。

## EventId 与日志封装

各模块继续通过 `LoggerMessage` 目录维护有语义的领域日志，调用处不改为重复的 `ILogger` 模板。仅删除零调用事件，并合并消息模板、级别和参数完全一致的事件。当前事件编号范围为：Router `3000–3048`、Engine（含 Host）`4000–4069`。

Router 的 Provider 可观测性事件在 Information 级别记录请求/响应方法、URI、状态码和 Header；请求/响应体为 Debug 级别、限长并对敏感 Header 脱敏。YARP 转发记录最终发送到 Provider 的请求 Header 和下游响应 Header，不缓冲流式响应体。

## 扩展原则

1. 通用请求日志与 OpenAgent Trace tag 由 Hosting middleware 横切采集；HTTP 请求量与耗时直接使用 ASP.NET Core instrumentation，业务代码不重复写生命周期埋点或 SDK 已有指标。
2. 业务代码只保留领域决策、降级和异常等有语义事件，并只依赖 `ILogger<T>`、`ActivitySource` 和 `Meter`，不直接依赖具体观测后端。
3. Logs、Traces 和 Metrics 都可统一通过 OTLP 交给部署侧 Collector；Console 和 Prometheus Pull 仍作为独立本地/拉取出口。
4. 应用不包含供应商专用存储 exporter；新增自定义 Meter 时必须将其名称配置为服务的 `OpenTelemetrySource`。
5. 任何高基数上下文保留在日志或 trace attribute 中，不进入 metric tag。

## 本地验证

```bash
curl -fsS http://localhost:5001/health
curl -fsS http://localhost:5001/ready
curl -fsS http://localhost:5001/metrics
```

发起一次 Router 请求后，可以检查业务指标是否出现：

```bash
curl -fsS http://localhost:5001/metrics \
  | grep 'http_server_request_duration\|openagent_router_forwarding_failures_total\|openagent_router_provider_selections_total\|openagent_router_cache_hits_total'
```
