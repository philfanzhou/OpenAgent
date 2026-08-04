# OpenAgent 日志 Label 设计与多实例扩展建议

> 项目: `OpenAgent`
> 日期: 2026-06-26
> 状态: 待实施建议
> 范围: Engine / Router 当前 Serilog + Loki 日志链路，以及未来多实例、多应用部署下的 label 设计。

---

## 一、结论摘要

当前日志链路里确实存在两套名称相近的字段：

| 字段 | 类型 | 当前值 | 来源 | 作用 |
|---|---|---|---|---|
| `service` | Loki label | `OpenAgent.Engine` / `OpenAgent.Engine` | Engine / Router 的 `appsettings*.json` | Loki 索引、Grafana label 查询 |
| `ServiceName` | Serilog 日志属性 | `agent-engine` / `agent-router` | `UseAgentSerilog(...)` enrich | 单条日志事件属性 |
| `env` | Loki label | `prod` | `deploy/docker-compose.yml` 环境变量覆盖 | 区分部署环境 |
| `ServiceVersion` | Serilog 日志属性 | `1.0.0` | `UseAgentSerilog(...)` enrich | 单条日志事件属性 |
| `InstanceId` | Serilog 日志属性 | `Environment.MachineName` | `UseAgentSerilog(...)` enrich | 单条日志事件属性 |
| `MachineName` | Serilog 日志属性 | 当前机器名 / 容器主机名 | `Serilog.Enrichers.Environment` | 单条日志事件属性 |
| `ThreadId` | Serilog 日志属性 | 当前线程 ID | `Serilog.Enrichers.Thread` | 单条日志事件属性 |

核心判断：

- 当前单应用、少量服务、单实例场景基本够用。
- 未来多实例、多应用场景不够清晰，主要问题是 `service` 与 `ServiceName` 含义重复但值不一致。
- 不建议把 `TenantId`、`UserId`、`AgentId`、`ConversationId`、`TraceId` 等高基数字段做成 Loki label；它们保留为日志属性更合理。
- 推荐先统一命名和值，再按部署复杂度逐步增加低基数 label。

---

## 二、当前代码证据

### 2.1 Loki 静态 label

Engine:

```json
"labels": [
  { "key": "service", "value": "OpenAgent.Engine" }
]
```

位置:

- `Backend/OpenAgent/Agent.Engine/src/Host/appsettings.json`
- `Backend/OpenAgent/Agent.Engine/src/Host/appsettings.Development.json`

Router:

```json
"labels": [
  { "key": "service", "value": "OpenAgent.Engine" }
]
```

位置:

- `Backend/OpenAgent/Agent.Router/src/Router/appsettings.json`
- `Backend/OpenAgent/Agent.Router/src/Router/appsettings.Development.json`

生产 compose 额外追加:

```yaml
Serilog__WriteTo__1__Args__labels__1__key=env
Serilog__WriteTo__1__Args__labels__1__value=prod
```

位置:

- `deploy/docker-compose.yml`

因此当前 Loki 实际可用于索引查询的主要 label 是：

```text
service = OpenAgent.Engine / OpenAgent.Engine
env     = prod
```

### 2.2 Serilog 事件属性

共享日志初始化在:

```csharp
loggerConfiguration
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.WithProperty("ServiceName", serviceName)
    .Enrich.WithProperty("ServiceVersion", serviceVersion)
    .Enrich.WithProperty("InstanceId", Environment.MachineName)
```

位置:

- `Backend/OpenAgent/Agent.Hosting/src/SerilogHostBuilderExtensions.cs`

Engine 启动传入:

```csharp
builder.Host.UseAgentSerilog("agent-engine");
```

Router 启动传入:

```csharp
builder.Host.UseAgentSerilog("agent-router");
```

这些字段会进入每条结构化日志事件，但当前没有配置 `propertiesAsLabels`，所以它们不会自动变成 Loki label。

---

## 三、为什么会看到 service 和 ServiceName 类似重复

这是两个层面的信息混在一起显示：

1. `service` 是 Loki label，适合做 Grafana 顶层筛选，例如 `{service="OpenAgent.Engine"}`。
2. `ServiceName` 是 Serilog 属性，跟随每条日志作为结构化字段输出。

两者表达的都是“哪个服务产生了日志”，但当前命名和值不一致：

```text
service     = OpenAgent.Engine
ServiceName = agent-engine

service     = OpenAgent.Engine
ServiceName = agent-router
```

这会带来几个实际问题：

- Grafana 查询时不知道该用 `service` 还是 `ServiceName`。
- 面板变量和值显示不统一。
- 未来接入 OpenTelemetry、Promtail 或其他服务时，命名风格容易继续分叉。
- 多应用共享 Loki 时，仅靠 `service` 无法表达“属于哪个产品 / 应用族”。

---

## 四、未来多实例、多应用是否够用

### 4.1 当前方案能支撑的场景

当前标签适合：

- 一个 Loki/Grafana 只服务 OpenAgent。
- Engine 和 Router 各一个实例。
- 主要按服务和环境查看日志。

典型查询：

```logql
{service="OpenAgent.Engine", env="prod"}
```

### 4.2 当前方案不足的场景

如果未来出现以下情况，当前标签会不够：

- 同一个 Loki 接入多个应用，例如 OpenAgent、其他内部系统、测试平台。
- Engine 多实例部署，例如 `engine-1`、`engine-2`、`engine-3`。
- Router 多实例部署，例如多个网关节点。
- 多环境共用 Loki，例如 `dev`、`staging`、`prod`。
- 多集群、多机房部署，例如 `shanghai-prod`、`us-prod`。
- 需要按版本排查发布问题。

当前最大短板是：没有稳定的 `app` / `instance` / `version` / `cluster` 这类维度，且 `service` 与 `ServiceName` 不统一。

---

## 五、推荐 Label 方案

### 5.1 推荐低基数 Loki label

建议未来统一为：

| Label | 示例 | 是否建议现在加入 | 说明 |
|---|---|---|---|
| `app` | `open-agent` | 建议 | 区分应用族；多应用共享 Loki 时很重要 |
| `service` | `agent-engine` / `agent-router` | 建议 | 区分服务；应与 `ServiceName` 保持同值 |
| `env` | `dev` / `staging` / `prod` | 建议 | 区分环境 |
| `instance` | `engine-1` / `router-1` / pod name | 多实例时加入 | 区分副本；实例数量可控，适合排障 |
| `version` | `1.0.0` / git sha | 可选 | 发布排障有用；版本数量通常可控 |
| `cluster` | `shanghai-prod` | 多集群时加入 | 多机房、多集群时使用 |

推荐查询示例：

```logql
{app="open-agent", service="agent-engine", env="prod"}
```

多实例排查：

```logql
{app="open-agent", service="agent-engine", env="prod", instance="engine-1"}
```

### 5.2 不建议作为 Loki label 的字段

以下字段不建议放入 Loki label：

| 字段 | 原因 | 推荐位置 |
|---|---|---|
| `TraceId` | 每次请求几乎不同，基数极高 | 日志属性 |
| `ConversationId` | 会话数量持续增长，基数极高 | 日志属性 |
| `UserId` | 用户数量增长，且涉及隐私 | 日志属性，必要时脱敏 |
| `TenantId` | 租户多时基数高，且容易造成索引膨胀 | 日志属性 |
| `AgentId` | Agent 数量可能增长，适合按属性检索 | 日志属性 |
| `Query` | 内容高基数且可能敏感 | 日志属性，必要时脱敏或限长 |
| `ThreadId` | 排障价值有限，基数不稳定 | 日志属性 |

Loki label 的原则是：少量、稳定、低基数、常用于第一层筛选。

---

## 六、建议的落地步骤

### 6.1 第一阶段：统一 service 命名

把 Loki label 的 `service` 值调整为与 `UseAgentSerilog(...)` 传入值一致：

```text
OpenAgent.Engine -> agent-engine
OpenAgent.Engine -> agent-router
```

理由：

- `service` 是 Grafana 查询入口，应使用短小稳定的服务标识。
- `ServiceName` 已经采用 `agent-engine` / `agent-router`，统一后减少理解成本。
- 未来接入 OpenTelemetry 的 `service.name` 时也更自然。

### 6.2 第二阶段：增加 app 和 env

在 Engine / Router 的 Loki label 中固定加入：

```text
app = open-agent
env = dev / staging / prod
```

建议 `env` 继续由部署环境覆盖，不要硬编码成单一值。

### 6.3 第三阶段：多实例时增加 instance

多实例部署后，为每个副本提供稳定实例标识：

```text
instance = engine-1
instance = engine-2
instance = router-1
```

在 Docker Compose 场景可以用环境变量覆盖：

```yaml
Serilog__WriteTo__1__Args__labels__3__key=instance
Serilog__WriteTo__1__Args__labels__3__value=engine-1
```

在 Kubernetes 场景建议使用 pod name 或 workload instance id。

### 6.4 第四阶段：按需要增加 version / cluster

如果发布排障频繁，可以增加：

```text
version = 1.0.0
```

如果未来有多集群或多机房：

```text
cluster = shanghai-prod
```

这些字段不要一开始全加，按真实部署复杂度逐步启用。

---

## 七、建议配置示例

### 7.1 Engine

```json
"labels": [
  { "key": "app", "value": "open-agent" },
  { "key": "service", "value": "agent-engine" }
]
```

生产环境通过环境变量追加：

```text
Serilog__WriteTo__1__Args__labels__2__key=env
Serilog__WriteTo__1__Args__labels__2__value=prod
Serilog__WriteTo__1__Args__labels__3__key=instance
Serilog__WriteTo__1__Args__labels__3__value=engine-1
```

### 7.2 Router

```json
"labels": [
  { "key": "app", "value": "open-agent" },
  { "key": "service", "value": "agent-router" }
]
```

生产环境通过环境变量追加：

```text
Serilog__WriteTo__1__Args__labels__2__key=env
Serilog__WriteTo__1__Args__labels__2__value=prod
Serilog__WriteTo__1__Args__labels__3__key=instance
Serilog__WriteTo__1__Args__labels__3__value=router-1
```

---

## 八、是否使用 propertiesAsLabels

`Serilog.Sinks.Grafana.Loki` 支持 `propertiesAsLabels`，可以把日志事件属性提升为 Loki label。

但本项目当前不建议大范围使用它。

原因：

- 一旦误把 `ConversationId`、`TraceId`、`UserId` 等字段提升为 label，会造成 Loki label 基数爆炸。
- 当前需要成为 label 的字段都可以通过静态配置或部署环境变量明确设置。
- 显式配置 `labels` 比自动提升属性更可控，适合当前阶段。

如果未来需要使用，只建议提升低基数字段，例如：

```json
"propertiesAsLabels": [
  "ServiceName"
]
```

但更推荐直接把 `service` label 配准，而不是再依赖 `ServiceName` 提升。

---

## 九、最终建议

短期建议：

1. 把 `service` label 的值统一成 `agent-engine` / `agent-router`。
2. 新增 `app=open-agent` label。
3. 保留 `env` 由部署环境覆盖。
4. 不把 `TraceId`、`ConversationId`、`TenantId`、`UserId`、`AgentId` 设为 Loki label。

中期建议：

1. 多实例部署时新增 `instance` label。
2. 发布排障有需要时新增 `version` label。
3. 多集群后新增 `cluster` label。

这套方案的目标是让 Grafana 查询入口清晰：

```text
先按 app 找到应用
再按 service 找到服务
再按 env / instance 缩小部署范围
最后用 TraceId / ConversationId / AgentId 等日志属性做精确排查
```

这样既能支撑多实例、多应用，也能避免 Loki label 过多导致索引膨胀。
