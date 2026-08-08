# Trace / Logs / Metrics 快速排查

本文用于 Router 与 Engine 请求异常的现场取证。当前信号与配置边界见 [`overview/Observability.md`](./overview/Observability.md)。

## 先收集最小现场

优先记录：

- 请求发生时间与时区；
- URL、HTTP 方法、状态码；
- 响应头或响应体中的 `X-Trace-Id`；
- `conversationId`、请求或最终选择的 `agentId`；
- 是否为 SSE，以及最后一个已收到的事件；
- Router 与 Engine 实例地址。

不要把 API Key、Authorization、Cookie 或完整用户正文复制到排障记录。

## 1. 判断故障范围

分别检查 Router 与 Engine：

```bash
curl -i http://localhost:5001/health
curl -i http://localhost:5001/ready
curl -i http://localhost:5208/health
curl -i http://localhost:5208/ready
```

- `health` 失败：先处理进程、监听端口或启动配置。
- `health` 成功而 `ready` 失败：检查 Redis、注册发现或服务自身 readiness 检查。
- 两者均成功：继续按单次请求证据排查。

## 2. 从 Router 日志定位请求

Router 结构化日志输出到 stdout，并在配置 OTLP 时进入 OpenTelemetry Logs。用 `TraceId` 为第一检索条件，再使用时间、`ConversationId` 或 `AgentId` 缩小范围。重点事件：

| 事件 | 含义 |
|------|------|
| `Request completed` | Hosting 横切层确认请求完成，包含路由、状态和耗时 |
| `Agent selection completed` | 显式 Agent 或意图识别的最终选择 |
| `Forwarding failed` | YARP 未完成下游请求，检查 `ForwarderError` 和异常类型 |
| `Agent access denied` | 目标 Agent 不在当前用户可见范围 |

如果没有对应 Router 日志，优先检查入口地址、认证、代理层和时间窗口，而不是直接判断 Engine 故障。

## 3. 确认是否到达 Engine 或外部 Agent

对于 Engine 目标：

1. 用同一时间与关联字段检查 Engine stdout。
2. 核对 Router 日志中的目标 endpoint 与实际 Engine 注册地址。
3. 对 SSE 请求确认响应 `Content-Type` 为 `text/event-stream`，并判断失败发生在首字节之前还是流中。

对于第三方 Agent：

1. 核对 Router 选中的 external agent、adapter 与目标地址。
2. 检查外部 Agent 自身日志和健康接口。
3. 不要向外部目标透传 Router 内部身份头或调用者 Authorization；只使用该 Agent 配置的认证方式。

## 4. 使用 Metrics 判断是否为系统性问题

```bash
curl -fsS http://localhost:5001/metrics > /tmp/openagent-router.metrics
grep 'openagent_requests\|openagent_request_duration' /tmp/openagent-router.metrics
grep 'openagent_router_forwarding_failures_total' /tmp/openagent-router.metrics
```

- 路由计数增长而转发失败不增长：问题更可能发生在下游业务响应或客户端消费。
- 某个 `forwarder_error` 持续增长：检查目标解析、连接、超时和下游可用性。
- 指标不存在：先确认至少完成过一次相应操作，再检查服务的 `OpenTelemetrySource` 是否匹配 Meter 名称。

ASP.NET Core 运行时指标也会由 `/metrics` 暴露，可用于判断 HTTP 总体错误率和请求时长。不要把单条请求问题仅凭聚合指标下结论。

## 5. 使用分布式 Trace

仅当部署已配置 `OpenTelemetry:OtlpEndpoint` 或 `OTEL_EXPORTER_OTLP_ENDPOINT`，应用才会导出 trace。查询方式由部署使用的 Collector 与存储决定。

查询时同时保留：

- OpenAgent `X-Trace-Id`；
- W3C Activity Trace ID；
- Router 与 Engine 的 service name；
- 请求发生时间。

`X-Trace-Id` 与 Activity Trace ID 不保证相同。若观测后端中没有 span，先核对 OTLP 配置、Collector 接收状态与 service name；不能据此推断请求没有进入应用。

## 常见症状

| 症状 | 优先检查 |
|------|----------|
| 401 / 403 | Authentication 配置、凭据、Tenant 与 Agent ACL |
| 404 / 未找到 Agent | Agent catalog、发布状态、外部 Agent 配置、显式 `agentId` |
| 429 | Router rate limit 与客户端身份维度 |
| 502 / 503 | Engine registry、外部目标健康、目标 URL、连接错误 |
| 504 / 长时间无首包 | 意图识别超时、YARP 超时、下游模型首 token 延迟 |
| SSE 中途结束 | 最后事件、Engine 异常日志、客户端断开、下游流协议 |
| 选择了错误 Agent | 候选 Agent 描述、ACL 过滤结果、识别置信度与 fallback |

## 输出排障结论

结论至少包含：

1. 影响范围与发生时间；
2. 能证明请求经过哪些组件的日志、trace 或指标证据；
3. 第一个确定失败的边界；
4. 根因与仍待验证的假设分开书写；
5. 修复动作与验证方法。

如果现场只提供了代码而没有运行时信号，应明确标注为静态分析，不能写成已验证的线上结论。
