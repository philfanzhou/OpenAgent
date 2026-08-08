# 可观测性

本文是 OpenAgent 当前可观测性能力的唯一事实入口。它描述仓库内已经实现的信号、配置与责任边界；具体排障步骤见 [`../trace-troubleshoot.md`](../trace-troubleshoot.md)。

## 当前实现

| 信号 | 应用侧实现 | 暴露方式 | 当前边界 |
|------|------------|----------|----------|
| 日志 | 业务代码使用 `ILogger<T>`，由 Serilog 接管 | 结构化日志写入 Console/stdout | 日志采集、存储与查询后端由部署环境决定 |
| Trace | OpenTelemetry ASP.NET Core、HttpClient 与服务自定义 `ActivitySource` | 配置 OTLP 地址后导出 | 仓库不内置 Collector 或 Trace 存储 |
| Metrics | OpenTelemetry ASP.NET Core 指标与服务自定义 `Meter` | Prometheus `/metrics` | 抓取、长期存储与看板由部署环境决定 |
| Health | ASP.NET Core Health Checks | `/health`、`/ready`，兼容 `/health/live`、`/health/ready` | `ready` 用于依赖可用性，`health` 用于进程存活 |

应用与基础设施的边界如下：

```text
ILogger<T> -> Serilog -> Console/stdout -> 部署环境可选日志采集器

ASP.NET Core / HttpClient / ActivitySource -> OpenTelemetry
                                             |-> OTLP（可选）
                                             `-> /metrics（Prometheus Pull）
```

仓库不绑定 Loki、Tempo、Grafana、Prometheus Server 或特定 Collector。部署可以选择这些组件，但不能把部署侧可用性当作应用代码的默认能力。

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
    "OtlpEndpoint": "http://otel-collector:4317"
  }
}
```

- `OpenTelemetry:OtlpEndpoint` 可省略；省略时不注册 OTLP trace exporter，`/metrics` 仍可用。
- 也可以使用标准环境变量 `OTEL_EXPORTER_OTLP_ENDPOINT`。
- OTLP 地址存在但不是绝对 HTTP(S) URI 时启动失败，避免服务看似正常但遥测静默丢失。
- `OpenTelemetry:ServiceName` 与 `ServiceVersion` 可以覆盖应用默认值。
- Serilog 的输出和最小级别由各服务 `appsettings*.json` 中的 `Serilog` 节控制。

## 指标

Router 当前提供以下业务指标：

| 指标 | 标签 | 含义 |
|------|------|------|
| `openagent_router_routes_total` | `action`、`status` | Router 接收和处理的路由请求数 |
| `openagent_router_forwarding_failures_total` | `action`、`forwarder_error` | YARP 转发失败数 |

标签值会被裁剪并转为小写，空值归一为 `unknown`。新增指标必须保持低基数；不能把 `TraceId`、`ConversationId`、`AgentId`、`UserId`、`TenantId`、URL 或异常文本放入指标标签。

## 日志属性与 Trace 标识

`ServiceName`、`ServiceVersion`、`InstanceId` 是所有 Serilog 事件的稳定属性。请求日志按上下文记录 `TraceId`、`ConversationId`、`AgentId`、`UserId`、`TenantId` 等高基数字段，这些字段用于单次请求定位，不应提升为日志存储的索引标签。

`X-Trace-Id` 是 OpenAgent 的请求关联标识；W3C `traceparent` 中的 Activity Trace ID 是分布式追踪标识。两者都应在排障证据中保留，但不能假设它们始终相同。

## 扩展原则

1. 业务代码只依赖 `ILogger<T>`、`ActivitySource` 和 `Meter`，不直接依赖具体观测后端。
2. 日志保持单一 stdout 出口，避免应用同时直推多个存储造成重试、背压和重复数据。
3. Trace 统一通过 OTLP 交给部署侧 Collector；应用不包含供应商专用 exporter。
4. Metrics 使用 Prometheus Pull；新增自定义 Meter 时必须将其名称配置为服务的 `OpenTelemetrySource`。
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
  | grep 'openagent_router_routes_total\|openagent_router_forwarding_failures_total'
```
