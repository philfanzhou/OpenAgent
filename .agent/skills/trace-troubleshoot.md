# Trace / Logs / Metrics 快速排查

## 用途

当用户要求“排查问题”、“请求无响应”、“查日志”、“查 trace”、“看 metrics”、“为什么失败/超时/卡住”时，优先使用本技能。目标不是只阅读代码或猜原因，而是连接实际服务，从 Grafana、Loki、Tempo、Prometheus 获取证据后输出排查报告。

本技能适用于：

- 单次请求失败、无响应、超时、SSE 不返回、流式响应卡住。
- 已有 `trace_id`、`conversation_id`、`agent_id`、发生时间、用户描述或日志片段。
- 需要判断问题发生在 Router、Engine、模型调用、工具调用、MCP、Redis、归档库、前端展示还是部署链路。
- 需要确认问题是单点偶发还是系统性异常。

本技能负责已经发生请求的可观测性取证；本地服务未启动、端口冲突、配置或协议联调问题
使用 `.agent/prompts/debug-integration.md`。

## 触发短语

用户出现以下表达时，应优先命中本技能：

- 排查问题、帮我查一下、请求无响应、接口没返回、一直转圈、超时了。
- 查日志、看 Loki、看 Grafana、看 trace、查 Tempo、看 metrics、查 Prometheus。
- 这个 trace 怎么回事、这个 conversation 为什么失败、工具调用为什么失败、MCP 是否有问题。
- 看看 Router / Engine / TestChat / AuditStation 哪里出问题。

## 输入参数

尽量从用户描述或现场补齐下列参数。缺失时先按最近 30 分钟查询。

| 参数 | 必填 | 示例 | 说明 |
| --- | --- | --- | --- |
| `SERVER_IP` | 否 | `<observability-host>` | 观测栈服务器；默认使用当前部署配置中的观测栈地址。 |
| `TIME_RANGE` | 否 | `30m`、`2h` | Loki query_range 的 `since` 窗口，默认 `30m`。 |
| `TRACE_ID` | 否 | `4bf92f3577b34da6a3ce929d0e0e4736` | W3C 32 位 trace id。 |
| `CONVERSATION_ID` | 否 | `conv-xxx` | 会话 id。 |
| `AGENT_ID` | 否 | `agent-xxx` | Agent 配置 id。 |
| `SERVICE` | 否 | `agent-engine` | `agent-router`、`agent-engine`、`auditstation` 等。 |
| `KEYWORD` | 否 | `timeout` | 用户提供的错误关键字、异常文本、邮箱、工具名等。 |
| `GRAFANA_API_KEY` | 否 | `glsa_xxx` | 需要通过 Grafana API 查询时使用；直接查数据源不需要。 |

建议先在 shell 中设置：

```bash
export SERVER_IP="${SERVER_IP:-<observability-host>}"
export TIME_RANGE="${TIME_RANGE:-30m}"
export GRAFANA_URL="${GRAFANA_URL:-http://${SERVER_IP}:3000}"
export LOKI_URL="${LOKI_URL:-http://${SERVER_IP}:3100}"
export TEMPO_URL="${TEMPO_URL:-http://${SERVER_IP}:3200}"
export PROM_URL="${PROM_URL:-http://${SERVER_IP}:9090}"
```

## 服务入口

| 组件 | 默认地址 | 用途 |
| --- | --- | --- |
| Grafana | `http://${SERVER_IP}:3000` | Dashboard、Explore、Logs/Trace/Metrics 互跳。 |
| Loki | `http://${SERVER_IP}:3100` | 结构化日志。 |
| Tempo | `http://${SERVER_IP}:3200` | 分布式 trace。 |
| Prometheus | `http://${SERVER_IP}:9090` | 指标、健康和趋势。 |
| Engine metrics | `https://${SERVER_IP}:9001/metrics` | Engine Prometheus exporter。 |
| Router metrics | `https://${SERVER_IP}:9002/metrics` | Router Prometheus exporter。 |

Grafana 数据源 UID：

| 数据源 | UID |
| --- | --- |
| Loki | `loki` |
| Tempo | `tempo` |
| Prometheus | `prometheus` |

## 排查原则

1. 先确认请求有没有进入系统：Router 日志、Router metrics、入口时间点。
2. 再确认是否转发到 Engine：Router 转发日志、forwarding failure 指标、Engine 日志。
3. 有 `trace_id` 时，以 trace 为锚点查 Loki 和 Tempo。
4. 没有 `trace_id` 时，以时间窗口 + `conversation_id` / `agent_id` / 关键词查 Loki，再反查 trace。
5. Metrics 用于判断范围：是否只有单次请求异常，还是服务整体失败率、延迟、工具调用出现波动。
6. 结论必须引用证据：关键日志、错误 span、PromQL 查询结果或 Dashboard 观察点。

## 0. 基础连通性检查

先确认观测栈和业务服务是否可访问：

```bash
curl -fsS "${GRAFANA_URL}/api/health"
curl -fsS "${LOKI_URL}/ready"
curl -fsS "${TEMPO_URL}/ready"
curl -fsS "${PROM_URL}/-/ready"
```

检查 Prometheus 是否正在抓取 Router / Engine：

```bash
curl -G "${PROM_URL}/api/v1/query" \
  --data-urlencode 'query=up{job=~"agent-engine|agent-router"}'
```

如果 `up` 为 0 或没有序列，先排查部署、端口、证书或 Prometheus scrape 配置，不要直接判断业务代码有问题。

## 1. 有 trace_id 时的单请求排查

Router、Engine 和 Channels 使用 `X-Trace-Id` 传播调用链标识。排查时同时检查入口 header、
下游转发 header、日志 scope 的 `TraceId` 和 Activity trace id；已有 `X-Trace-Id` 时不得生成
新的无关 trace。Core tracing 中 tenant 缺失保持为 `null`，不要用伪造 tenant 污染检索维度。

设置 trace id：

```bash
export TRACE_ID="<32位trace_id>"
```

### 1.1 查 Loki 日志

优先查 OTLP 日志常见的 `service_name` 维度：

```bash
curl -G "${LOKI_URL}/loki/api/v1/query_range" \
  --data-urlencode 'query={service_name=~"agent-router|agent-engine"} |= "'"${TRACE_ID}"'"' \
  --data-urlencode "since=${TIME_RANGE}" \
  --data-urlencode "limit=200"
```

如果没有结果，再查历史 Loki label 命名：

```bash
curl -G "${LOKI_URL}/loki/api/v1/query_range" \
  --data-urlencode 'query={service=~"agent-router|agent-engine|OpenAgent.Router|OpenAgent.Engine"} |= "'"${TRACE_ID}"'"' \
  --data-urlencode "since=${TIME_RANGE}" \
  --data-urlencode "limit=200"
```

从日志中记录：

- 请求入口、目标服务、endpoint/action。
- `TraceId` / `trace_id` / `span_id`。
- `ConversationId`、`AgentId`、`TenantId`，只作为过滤字段，不作为 Loki label。
- `error_code`、`FailureStage`、`ExceptionType`、`ExceptionMessage`。
- `DurationMs`、模型首 token、工具调用次数、工具名、MCP 错误。
- Router `forwarder_error` 或 Engine 执行摘要 `AgentExecutionSummary`。

### 1.2 查 Tempo trace

```bash
curl -fsS "${TEMPO_URL}/api/traces/${TRACE_ID}"
```

重点看：

| 关注点 | 判断方式 |
| --- | --- |
| Router 是否有入站 span | 没有则请求可能没到 Router 或 trace 没传播。 |
| Router -> Engine 是否有 HTTP span | 没有则优先看路由、认证、限流、服务发现、YARP 转发。 |
| Engine span 是否结束 | 未结束或耗时异常，继续查模型/工具/上下文处理。 |
| 哪个 span `status=error` | 错误 span 是根因候选，不一定是最终报错点。 |
| 最耗时 span | 用于解释“无响应/慢”。 |

### 1.3 打开 Grafana 跳转入口

Grafana Explore 可以直接用以下入口人工查看互跳：

```bash
printf '%s\n' "${GRAFANA_URL}/explore"
printf '%s\n' "${GRAFANA_URL}/d/agent-observability/open-agent-observability"
```

在 Grafana 中：

1. Loki Explore 用 `trace_id` 过滤日志。
2. 点击日志 derived field 跳到 Tempo trace。
3. 在 Tempo trace 右侧使用 Trace to logs / Trace to metrics。
4. Dashboard 选择 `agent-engine` 或 `agent-router` 观察同一时间窗口。

## 2. 没有 trace_id 时的请求无响应排查

先确定时间窗口和线索：

```bash
export KEYWORD="<用户给出的关键词，可为空>"
export CONVERSATION_ID="<conversation_id，可为空>"
export AGENT_ID="<agent_id，可为空>"
```

### 2.1 查近期错误和超时

```bash
curl -G "${LOKI_URL}/loki/api/v1/query_range" \
  --data-urlencode 'query={service_name=~"agent-router|agent-engine"} |~ "(?i)timeout|exception|error|failed|cancel|无响应|tool|mcp"' \
  --data-urlencode "since=${TIME_RANGE}" \
  --data-urlencode "limit=200"
```

历史 label fallback：

```bash
curl -G "${LOKI_URL}/loki/api/v1/query_range" \
  --data-urlencode 'query={service=~"agent-router|agent-engine|OpenAgent.Router|OpenAgent.Engine"} |~ "(?i)timeout|exception|error|failed|cancel|tool|mcp"' \
  --data-urlencode "since=${TIME_RANGE}" \
  --data-urlencode "limit=200"
```

### 2.2 按 conversation_id / agent_id 过滤

```bash
curl -G "${LOKI_URL}/loki/api/v1/query_range" \
  --data-urlencode 'query={service_name=~"agent-router|agent-engine"} |= "'"${CONVERSATION_ID}"'"' \
  --data-urlencode "since=${TIME_RANGE}" \
  --data-urlencode "limit=200"
```

```bash
curl -G "${LOKI_URL}/loki/api/v1/query_range" \
  --data-urlencode 'query={service_name=~"agent-router|agent-engine"} |= "'"${AGENT_ID}"'"' \
  --data-urlencode "since=${TIME_RANGE}" \
  --data-urlencode "limit=200"
```

找到日志中的 `trace_id` 后，回到“有 trace_id 时的单请求排查”。

### 2.3 判断卡在哪一层

| 证据 | 初步判断 | 下一步 |
| --- | --- | --- |
| Router 没有请求日志 | 请求没到平台入口，查 Nginx、前端、网络、认证。 | 查入口日志和 Router `up`。 |
| Router 有请求，Engine 没日志 | 路由/服务发现/YARP 转发失败。 | 查 `openagent_router_forwarding_failures_total` 和 Router 错误日志。 |
| Engine 有开始，无结束摘要 | Engine 内部执行卡住或被取消。 | 查 Tempo 最耗时 span、模型/工具日志。 |
| Engine 完成但前端无响应 | SSE/NDJSON 输出、代理缓冲、前端解析或连接中断。 | 查 flush/streaming 日志和 TestChat 控制台。 |
| 工具调用开始后卡住 | MCP server、网络、参数、工具响应体。 | 查工具名、请求摘要、MCP 日志。 |
| 模型调用后卡住 | LLM provider、首 token 延迟、超时设置。 | 查 first-token 指标和模型异常日志。 |

## 3. Metrics 范围判断

请求无响应或失败时，必须用 metrics 判断是否系统性问题。

请求量：

```bash
curl -G "${PROM_URL}/api/v1/query" \
  --data-urlencode 'query=sum(rate(openagent_requests_total{service="agent-engine"}[5m])) * 60'
```

失败率：

```bash
curl -G "${PROM_URL}/api/v1/query" \
  --data-urlencode 'query=sum(rate(openagent_failures_total[5m])) / clamp_min(sum(rate(openagent_requests_total[5m])), 0.001) * 100'
```

请求 P95 延迟：

```bash
curl -G "${PROM_URL}/api/v1/query" \
  --data-urlencode 'query=histogram_quantile(0.95, sum(rate(openagent_request_duration_ms_milliseconds_bucket[5m])) by (le))'
```

模型首 token P95：

```bash
curl -G "${PROM_URL}/api/v1/query" \
  --data-urlencode 'query=histogram_quantile(0.95, sum(rate(openagent_model_first_token_ms_milliseconds_bucket[5m])) by (le))'
```

工具调用失败：

```bash
curl -G "${PROM_URL}/api/v1/query" \
  --data-urlencode 'query=sum(rate(openagent_tool_calls_total{status="error"}[5m])) by (tool_type)'
```

Router 转发失败：

```bash
curl -G "${PROM_URL}/api/v1/query" \
  --data-urlencode 'query=sum(rate(openagent_router_forwarding_failures_total[5m])) by (forwarder_error)'
```

Dashboard 入口：

```bash
printf '%s\n' "${GRAFANA_URL}/d/agent-observability/open-agent-observability"
```

重点面板：

- Requests / min
- Success Rate
- Avg Duration
- Failures / min
- Request Duration P50 / P95 / P99
- First Token Latency P95
- Tool Calls by Type
- Average Turns per Request
- Router Routes / min
- Router Forwarding Failures / min
- Recent Logs

## 4. Grafana API 查询方式

Grafana 需要 API key 时设置：

```bash
export GRAFANA_API_KEY="<grafana api key>"
```

列出数据源：

```bash
curl -fsS "${GRAFANA_URL}/api/datasources" \
  -H "Authorization: Bearer ${GRAFANA_API_KEY}"
```

Prometheus 通过 Grafana 查询：

```bash
curl -fsS -X POST "${GRAFANA_URL}/api/ds/query" \
  -H "Authorization: Bearer ${GRAFANA_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "from": "now-30m",
    "to": "now",
    "queries": [{
      "refId": "A",
      "datasource": { "uid": "prometheus" },
      "expr": "sum(rate(openagent_requests_total[5m])) * 60",
      "format": "time_series"
    }]
  }'
```

Loki 通过 Grafana 查询：

```bash
curl -fsS -X POST "${GRAFANA_URL}/api/ds/query" \
  -H "Authorization: Bearer ${GRAFANA_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "from": "now-30m",
    "to": "now",
    "queries": [{
      "refId": "A",
      "datasource": { "uid": "loki" },
      "expr": "{service_name=~\"agent-router|agent-engine\"} |= \"'"${TRACE_ID}"'\"",
      "queryType": "range",
      "maxLines": 200
    }]
  }'
```

## 5. 输出报告格式

排查完成后必须按以下格式输出，避免只给零散日志。

```markdown
## 请求排查报告

**排查对象**: <trace_id / conversation_id / 时间窗口 / 用户问题>
**时间窗口**: <开始 - 结束>
**查询入口**: <Loki / Tempo / Prometheus / Grafana Dashboard>
**涉及服务**: <agent-router / agent-engine / MCP / LLM / TestChat>

### 结论

<1-3 句话说明根因或当前最可能原因。没有证据时明确写“尚未定位”，不要猜。>

### 证据

| 类型 | 查询 | 结果 |
| --- | --- | --- |
| Logs | <LogQL 或 curl 摘要> | <关键日志、错误字段、trace_id> |
| Trace | <Tempo trace id> | <错误 span / 最耗时 span / 缺失 span> |
| Metrics | <PromQL> | <失败率、延迟、工具失败、Router 转发失败> |
| Grafana | <Dashboard / Explore 入口> | <面板观察结果> |

### 调用路径

| 顺序 | 服务 | 阶段 | 耗时 | 状态 | 证据 |
| --- | --- | --- | --- | --- | --- |
| 1 | Router | request received | <ms> | ok/error | <日志或 span> |
| 2 | Router | forward to Engine | <ms> | ok/error | <日志或 metric> |
| 3 | Engine | agent execution | <ms> | ok/error | <日志或 span> |
| 4 | Engine | model/tool call | <ms> | ok/error | <日志或 span> |

### 影响范围

- 单请求 / 单会话 / 单工具 / 单服务 / 系统性异常。
- 是否影响发送、写入、审计或前端展示。

### 建议动作

1. <立即可执行动作>
2. <需要用户确认或需要访问权限的动作>
3. <后续修复或补充埋点建议>
```

## 6. 常见结论模板

| 现象 | 证据组合 | 结论方向 |
| --- | --- | --- |
| 请求无响应但 Router 无日志 | Router `up=1`，Loki 无入口日志 | 入口、前端、Nginx、认证或网络问题。 |
| Router 有转发失败 | Router 日志 + `openagent_router_forwarding_failures_total` | Engine 实例不可用、服务发现或 YARP 转发异常。 |
| Engine 有开始无结束 | Engine start 日志存在，summary 缺失，trace span 长耗时 | Engine 执行卡住、取消或异常未被摘要记录。 |
| 首 token 很慢 | `openagent_model_first_token_ms` P95 高，模型 span 长 | LLM provider 延迟或模型侧超时。 |
| 工具失败 | tool span error + `openagent_tool_calls_total{status="error"}` | MCP server、工具参数、网络或权限问题。 |
| 日志可见但 Dashboard 无值 | Loki 有日志，Prometheus `up=0` 或 metric 缺失 | metrics exporter/scrape 配置问题。 |

## 7. 相关文档

- `deploy/docs/Observability-Design.md`
- `Backend/OpenAgent/Agent.Hosting/docs/Logging-Label-Design.md`
- `Backend/OpenAgent/Agent.Hosting/docs/Logging-Framework-Decision.md`
- `deploy/observability/docker-compose.yml`
- `deploy/observability/grafana/dashboards/agent-observability.json`
- `deploy/observability/grafana/provisioning/datasources/loki.yaml`
- `deploy/observability/grafana/provisioning/datasources/tempo.yaml`
- `deploy/observability/grafana/provisioning/datasources/prometheus.yaml`
- `.agent/skills/service-lifecycle.md`
- `.agent/prompts/debug-integration.md`
