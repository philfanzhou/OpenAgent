# OpenAgent 日志框架选型决策文档

> 项目: `OpenAgent`（.NET 8.0 多服务 Agent 平台 — Engine / Router / Core / Hosting / 未来扩展服务）
> 文档定位: OpenAgent 日志框架的**收口决策**，给出最终选型、许可证合规、迁移路径与扩展路线。
> 适用日期: 2026-06-25
> 状态: **已决策**（应用侧 Serilog + 服务端 Loki + Grafana，预留 Promtail 扩展点）

---

## 一、决策摘要

| 层 | 选型 | 许可证 | 一句话理由 |
|---|---|---|---|
| **应用日志 API** | `Microsoft.Extensions.Logging`（MEL） | Apache 2.0（内置） | 仓库已用，零侵入 |
| **应用日志实现** | **Serilog** | Apache 2.0 | 结构化日志事实标准，Sink 生态最丰富 |
| **Sink 推送路径（直推）** | `Serilog.Sinks.Grafana.Loki` | Apache 2.0 | 直接 HTTP 推到 Loki，最少组件 |
| **Sink 推送路径（兜底）** | `Serilog.Sinks.Console` | Apache 2.0 | 输出 stdout，兼容 Promtail 未来抓取 |
| **日志存储 / 查询** | **Grafana Loki** | AGPL-3.0 | 标签模型契合多服务，与 OTel 生态打通 |
| **可视化 / 告警** | **Grafana** | AGPL-3.0 | 业界最成熟的可观测性大屏 |
| **日志采集（未来扩展）** | **Grafana Promtail** | AGPL-3.0 | 多语言 / 容器 stdout 场景的兜底采集器 |
| **日志查询抽象（未来）** | `ILogQueryService` + 自研 MCP Server | Apache 2.0 | 给 AI Agent 消费，与具体后端解耦 |
| **暂不引入** | Seq / Elasticsearch / OpenSearch | — | 见 §6 退场说明 |

> 备查：AGPL-3.0 在 OpenAgent 内部运维场景下**自用豁免**，不构成商业风险；详见 §4。

---

## 二、为什么是这个组合——逐层论证

### 2.1 应用侧为什么是 Serilog

| 维度 | Serilog | NLog | log4net | MEL Provider |
|---|---|---|---|---|
| 结构化日志（key-value） | 一等公民 | 需配 layout | 非原生 | 依赖 Provider |
| 与现有 `ILogger<T>` 共存 | ✅ `Serilog.Extensions.Logging` 桥接 | ✅ `NLog.Extensions.Logging` | ⚠️ 桥接 | ✅ 原生 |
| Sink / Target 数量 | 200+ | 200+ | 少 | 少 |
| 异步 + 批量 | 原生 | 需 wrapper | 默认同步 | 视 Provider |
| 代码 vs 配置 | 优先代码，可 JSON 覆盖 | XML 为主 | XML 为主 | JSON |
| 维护活跃度 | 高 | 高 | 中 | 高 |
| 社区共识（.NET 8 时代） | **事实标准** | 主流 | 维护遗留 | 抽象层 |

> 仓库已用 `ILogger<T>` 67 处 → **应用层代码零修改**，只在 `Program.cs` 接管 Provider。

### 2.2 协议出口为什么用 Loki Sink 直推

三种把 .NET 日志推到服务端的姿势：

| 路径 | 组件 | 适用 | 我们的选择 |
|---|---|---|---|
| **A** | `Serilog.Sinks.Grafana.Loki` → HTTP 直推 Loki | 单一 .NET 栈 | ✅ **首选** |
| **B** | `Serilog.Sinks.Console` → stdout → Promtail → Loki | 多语言/容器化兜底 | ✅ **双写兜底** |
| **C** | `Serilog.Sinks.OpenTelemetry` → OTLP → Collector → Loki | 强 OTel 一致 | 暂不引（复杂度↑） |

**最终采用 A + B 双写**：
- **A** 走 Loki Sink 直推：少一个组件、低延迟、标签完整
- **B** 走 Console：未来加 Promtail 不用改业务代码，"开箱即兼容"

### 2.3 服务端为什么是 Loki + Grafana

| 维度 | Loki + Grafana | Seq | OpenSearch + Dashboards | ELK（Elastic 官方） |
|---|---|---|---|---|
| 许可证 | AGPL-3.0 | 商业 + 免费版限 5GB | Apache 2.0 | ELv2 / SSPL |
| 标签维度查询 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ |
| 全文检索 | ⭐⭐（需 bloom） | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| 资源占用 | **低** | 低 | 中–高 | 高 |
| 多服务聚合 | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ |
| 容器化 / K8s 友好 | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ |
| 与 OTel 现有管道一致 | ✅ OTLP 友好 | ✅ 2024+ 支持 | ✅ | ✅ |
| 多数据源统一面板 | ✅ Grafana 30+ 数据源 | ❌ 单一 | ❌ 单一 | ❌ 单一 |
| 迁移到 ELK 难度 | ⭐⭐（加双写） | ⭐⭐⭐ | ⭐⭐ | — |

**Loki 的核心优势：标签索引 + 块存储**——和本仓库的"多服务、Redis、tenantId/conversationId 维度"模型完全契合。

---

## 三、当前仓库的迁移路径与成本

### 3.1 迁移前的现状

- 已有 `Microsoft.Extensions.Logging` 在 Engine / Router / Core 共 67 处调用
- 已有 OpenTelemetry 全家桶（OTLP 出口、ASP.NET Core / Http Instrumentation）
- 日志**无集中收集**，仅本地 stdout
- 测试侧使用 FakeRedis（内存 Mock）+ SQLite，无外部日志依赖

### 3.2 改动面评估

| 改动点 | 文件 | 工作量 | 风险 |
|---|---|---|---|
| **新增 NuGet 包** | `Backend/src/OpenAgent.Hosting/OpenAgent.Hosting.csproj` | 5 分钟 | 零（纯加包） |
| **封装 `UseAgentSerilog()` 扩展** | `Backend/src/OpenAgent.Hosting/SerilogHostBuilderExtensions.cs` | 0.5h | 低（统一 Host 初始化） |
| **应用 MEL 接管** | Engine / Router `Program.cs` | 1h | 低（添加 2 行） |
| **`appsettings.json` 增加 Serilog 节** | Engine / Router | 0.5h | 零（新增字段） |
| **独立部署 loki/grafana 服务** | `deploy/observability/docker-compose.yml` | 0.5h | 低（一次性基础设施） |
| **Grafana 自动配 Loki 数据源** | `deploy/observability/grafana/provisioning/` | 0.2h | 零 |
| **业务层 67 处 `ILogger<T>` 调用** | 业务代码 | **零修改** | — |

**总改造工时估算：3 小时左右**（不含测试与压测）。

### 3.3 业务代码为何零改动

```
现有: ILogger<T> → Console.WriteLine
新:   ILogger<T> → Serilog Provider → {Console, Loki}
                ↑ MEL 抽象，Serilog 只是换了 Provider 实现
```

这是 MEL 设计的最大价值：**实现可换，调用不变**。

### 3.4 风险点与回退方案

| 风险 | 触发条件 | 回退方案 |
|---|---|---|
| Loki 启动失败 | 镜像版本 / 端口冲突 | 降级到只写 Console，业务不受影响 |
| Loki Sink 推送失败 | 网络 / 配额 | 异常不会抛到业务线程（异步 + 重试） |
| AGPL 合规争议 | 商业产品嵌入 | 用 Apache 2.0 后端替换 Loki（详见 §4.3） |
| 标签基数爆炸 | 把 `conversationId` 误打成 label | 重命名 → properties 字段（详见 §5.4） |
| OTLP 协议版本不兼容 | 服务端升级 | 锁版本（详见 §5.5） |

---

## 四、商业授权合规性分析

### 4.1 许可证对照表

| 组件 | 许可证 | OpenAgent 场景是否合规 |
|---|---|---|
| Serilog 全家桶 | Apache 2.0 | ✅ 完全合规 |
| Microsoft.Extensions.Logging | Apache 2.0 | ✅ 完全合规 |
| `Serilog.Sinks.Grafana.Loki` | Apache 2.0 | ✅ 完全合规 |
| **Grafana Loki** | **AGPL-3.0** | ✅ **内部运维自用豁免** |
| **Grafana** | **AGPL-3.0** | ✅ **内部运维自用豁免** |
| **Grafana Promtail** | **AGPL-3.0** | ✅ **内部运维自用豁免** |
| 自研 MCP Server | Apache 2.0（采用本仓库授权惯例） | ✅ 合规 |
| ~~Seq~~ | 商业（免费版限 5GB / 单用户） | ❌ 超量需付费 |
| ~~Elasticsearch 7.11+~~ | ELv2 / SSPL | ⚠️ 禁托管服务，自用可，对外服务禁 |
| ~~OpenSearch~~ | Apache 2.0 | ✅ 完全合规（如果未来要替换 Loki） |

### 4.2 AGPL-3.0 的"传染边界"——重点澄清

**AGPL 唯一的"传染点"是**：

> 当你**修改了 AGPL 源码**并且**通过网络向外部用户提供服务**时，**修改部分必须开源**。

**OpenAgent 部署 Grafana 的三种场景判定**：

| 场景 | 是否触发 AGPL 传染 | 结论 |
|---|---|---|
| 公司内部运维，工程师登录 Grafana 看日志 | 否（不向"外部用户"提供） | ✅ 完全合法 |
| 客户公司部署 OpenAgent 一体化交付，自带 Grafana | **视情况**——若只"运行"不"修改" Grafana | ✅ 合法 |
| 把 Grafana 二次开发后嵌入自家产品对外卖 SaaS | **是**——AGPL 触发 | ❌ 需开源 Grafana 修改部分 |

> **本项目自我约束**：Grafana 仅作内部运维工具使用，**禁止**对其源码做二次修改后对外发布。

### 4.3 真要"零授权风险"时的备选

如果未来公司策略要求**彻底规避 AGPL**，技术上的应对路径是：

- 用 Apache 2.0 的搜索后端（OpenSearch 之类）替换 Loki
- 用 Apache 2.0 的可视化组件替换 Grafana
- Serilog 层**完全不动**——因为 Serilog 的多 Sink 抽象是切换成本可控的关键

> 风险点的本质：不是"用了什么"，而是"**Serilog 抽象**让切换可控"。

---

## 五、最终落地架构

### 5.1 数据流

```
┌─────────────────────────────────────────────────────────────┐
│  .NET 8 服务（Engine / Router / Core / Hosting）              │
│                                                             │
│  业务代码: ILogger<T>                                        │
│       │                                                     │
│       ▼  (MEL 抽象)                                          │
│  ┌──────────────────────────────────────┐                   │
│  │  Serilog.AspNetCore                  │                   │
│  │  (UseSerilog + ReadFrom.Configuration)                   │
│  ├──────────────────────────────────────┤                   │
│  │  Sink A: Console  →  stdout          │ ← 兜底            │
│  │  Sink B: GrafanaLoki → HTTP → Loki  │ ← 主路径          │
│  └──────────────────────────────────────┘                   │
└─────────────────────────────────────────────────────────────┘
                                │
                ┌───────────────┴───────────────┐
                ▼                               ▼
        ┌──────────────┐                 ┌──────────────┐
        │  Promtail    │ ← 抓 stdout      │   Loki       │
        │  (未来扩展)    │                 │  (3.3.x)     │
        │  DaemonSet / │                 │  端口 3100   │
        │  sidecar     │                 │  retention   │
        └──────┬───────┘                 └──────┬───────┘
               │                                │
               └─────────────┬──────────────────┘
                             ▼
                      ┌──────────────┐
                      │   Grafana    │
                      │  (11.3.x)    │
                      │  端口 3000   │
                      │  看板/告警    │
                      └──────────────┘
                             ▲
                             │ 查询 (LogQL)
                             │
                ┌────────────┴────────────┐
                │ 自研 MCP Server (未来)   │
                │ - search_logs           │
                │ - get_event             │
                │ - tail_by_filter        │
                └────────────┬────────────┘
                             │
                             ▼
                  ┌──────────────────┐
                  │  AI Agent / LLM  │
                  └──────────────────┘
```

### 5.2 组件版本与最小资源

| 组件 | 版本 | CPU | 内存 | 磁盘 |
|---|---|---|---|---|
| Loki | 3.3.x | 1–2 核 | 1–2 GB | 50–200 GB（30 天） |
| Grafana | 11.3.x | 0.5 核 | 512 MB | 1 GB |
| Promtail（未来） | 3.3.x | 0.2 核 | 128 MB | 极小 |
| .NET 应用 | 既有 | 既有 | 既有 | — |

> 单机 **2C4G 即可起**，30 天保留期约 50GB 起跳。

### 5.3 标签约定（必须在引入时统一）

> ⚠️ **Loki 黄金法则：label 必须是低基数（< 几百种取值），高基数字段放 properties。**

| 字段 | 提升为 label？ | 理由 |
|---|---|---|
| `service` | ✅ | 5 个服务以内 |
| `instance` | ✅ | Engine 实例数 × 服务 |
| `env` | ✅ | dev/staging/prod |
| `level` | ✅ | 4–5 个值 |
| `tenantId` | ⚠️ 慎用 | 视租户数（< 500 可接受） |
| `conversationId` | ❌ | 高基数，放 properties |
| `requestId` / `traceId` | ❌ | 高基数，放 properties |
| `agentId` | ⚠️ 视数量 | < 200 可接受 |

**约定统一在 Serilog Enrich 层注入**：
```csharp
.Enrich.FromLogContext()
.Enrich.WithMachineName()
.Enrich.WithProperty("ServiceName", serviceName)
.Enrich.WithProperty("InstanceId", instanceId)
```

### 5.4 Loki 保留期与清理策略

```yaml
compactor:
  working_directory: /loki/compactor
  retention_enabled: true
  retention_delete_delay: 2h

limits_config:
  retention_period: 720h        # 30 天
  ingestion_rate_mb: 16
  ingestion_burst_size_mb: 32
```

如果用对象存储（S3 / MinIO），建议同时配置对象存储的**生命周期策略**做双保险：Loki retention 设 30 天，对象存储设 35 天，让"应用层 + 存储层"双重清理互不冲突。

### 5.5 OTLP 协议版本约定

> 与现有 OTel 管线一致，使用 **OTLP v1 稳定协议**（2023-11-01 起）。

### 5.6 Docker 部署完整配置

Loki + Grafana 是独立可观测性基础设施，通过 `deploy/observability/docker-compose.yml` 单独部署。它通常只部署一次，后续 Engine / Router 发布不需要跟着重启。

业务服务只需要把 Serilog Sink 指向可访问的 Loki 地址：

- 如果业务服务和 Loki 在同一个 Docker 网络内，可以使用 `http://loki:3100`
- 如果 Loki 是单独服务器或单独 Compose project 暴露端口，使用 `http://<loki-host>:3100`
- 不要求把 Loki / Grafana 合并进主业务 `deploy/docker-compose.yml`

#### 5.6.1 目录约定

```
deploy/
├── docker-compose.yml
└── observability/
    ├── docker-compose.yml
    ├── loki/
    │   └── local-config.yaml
    └── grafana/
        └── provisioning/
            ├── datasources/
            │   └── loki.yaml
            └── dashboards/
                └── default.yaml
```

#### 5.6.2 业务服务环境变量（主 `deploy/docker-compose.yml`）

```yaml
services:
  # ── 业务服务（节选示例：Agent.Engine）──
  agent-engine:
    image: openagent/engine:latest
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      # Serilog → Loki 直推地址；按实际部署改成可访问的 Loki 地址
      - Serilog__WriteTo__1__Args__uri=http://<loki-host>:3100
      - Serilog__WriteTo__1__Args__labels__0__key=service
      - Serilog__WriteTo__1__Args__labels__0__value=OpenAgent.Engine
      - Serilog__WriteTo__1__Args__labels__1__key=env
      - Serilog__WriteTo__1__Args__labels__1__value=prod

  agent-router:
    image: openagent/engine:latest
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Serilog__WriteTo__1__Args__uri=http://<loki-host>:3100
      - Serilog__WriteTo__1__Args__labels__0__key=service
      - Serilog__WriteTo__1__Args__labels__0__value=OpenAgent.Engine
      - Serilog__WriteTo__1__Args__labels__1__key=env
      - Serilog__WriteTo__1__Args__labels__1__value=prod
```

> **关键点**：
> - `Serilog__WriteTo__1__Args__uri` 用下划线 `__` 分隔，对应 .NET 配置键 `Serilog:WriteTo:1:Args:uri`（数组下标 1 = 第二个 WriteTo = Loki）
> - `<loki-host>` 必须是 Engine / Router 容器里能访问到的地址，不一定是本机 `localhost`
> - 如果 Loki 和业务服务不在同一个 Docker 网络，不能直接依赖容器名 `loki`

#### 5.6.3 `deploy/observability/docker-compose.yml`

```yaml
services:
  # ── Loki 日志存储 ──
  loki:
    image: grafana/loki:3.3.2
    container_name: openagent_loki
    restart: unless-stopped
    command: -config.file=/etc/loki/local-config.yaml
    ports:
      - "3100:3100"
    volumes:
      - ./loki/local-config.yaml:/etc/loki/local-config.yaml:ro
      - loki-data:/loki
    healthcheck:
      test: ["CMD-SHELL", "wget --no-verbose --tries=1 --spider http://localhost:3100/ready || exit 1"]
      interval: 10s
      timeout: 3s
      retries: 5

  # ── Grafana 可视化 ──
  grafana:
    image: grafana/grafana:11.3.0
    container_name: openagent_grafana
    restart: unless-stopped
    ports:
      - "3000:3000"
    environment:
      - GF_SECURITY_ADMIN_USER=admin
      - GF_SECURITY_ADMIN_PASSWORD=${GRAFANA_ADMIN_PASSWORD:-admin}
      - GF_USERS_ALLOW_SIGN_UP=false
      # 关闭 Grafana 内置统计上报
      - GF_ANALYTICS_REPORTING_ENABLED=false
    volumes:
      - ./grafana/provisioning:/etc/grafana/provisioning:ro
      - grafana-data:/var/lib/grafana
    depends_on:
      loki:
        condition: service_healthy

volumes:
  loki-data:
  grafana-data:
```

> Grafana 初始 admin 密码用 `.env` 文件或环境变量注入，**禁止**直接硬编码进 yml。

#### 5.6.4 `deploy/observability/loki/local-config.yaml`

```yaml
auth_enabled: false

server:
  http_listen_port: 3100
  log_level: info

common:
  instance_addr: 127.0.0.1
  path_prefix: /loki
  storage:
    filesystem:
      chunks_directory: /loki/chunks
      rules_directory: /loki/rules
  replication_factor: 1
  ring:
    kvstore:
      store: inmemory

schema_config:
  configs:
    - from: "2024-01-01"
      store: tsdb
      object_store: filesystem
      schema: v13
      index:
        prefix: index_
        period: 24h

limits_config:
  # 默认租户保留 30 天；按租户差异保留可走 tenants_retention 文件
  retention_period: 720h
  ingestion_rate_mb: 16
  ingestion_burst_size_mb: 32
  per_tenant_rate_limit_bytes: 10485760

# ── 自动清理（见 §5.4）──
compactor:
  working_directory: /loki/compactor
  compaction_interval: 10m
  retention_enabled: true
  retention_delete_delay: 2h
  retention_delete_worker_count: 150
  delete_request_store: filesystem

# ── Schema 验证（部署时静态校验用）──
ruler:
  storage:
    type: local
    local:
      directory: /loki/rules
  alertmanager_url: http://localhost:9093
```

#### 5.6.5 `deploy/observability/grafana/provisioning/datasources/loki.yaml`

让 Grafana 启动时**自动注册** Loki 数据源，省去手动配：

```yaml
apiVersion: 1

datasources:
  - name: Loki
    type: loki
    access: proxy
    url: http://loki:3100
    isDefault: true
    jsonData:
      httpMethod: POST
      maxLines: 1000
    editable: false
```

> `isDefault: true` 让 Explore 模式默认就指向 Loki，运维零配置。

#### 5.6.6 `deploy/observability/grafana/provisioning/dashboards/default.yaml`（可选）

```yaml
apiVersion: 1

providers:
  - name: 'OpenAgent 默认仪表盘'
    orgId: 1
    folder: 'OpenAgent'
    type: file
    disableDeletion: false
    editable: true
    options:
      path: /var/lib/grafana/dashboards
```

把预制 JSON 面板放到 `grafana-data/dashboards/`，启动时自动加载。**本期不强制要求**，先通过 Explore 跑 LogQL 即可。

#### 5.6.7 启动与验证

```bash
# 1. 单独部署 Loki / Grafana（通常只做一次）
cd deploy/observability
docker compose up -d

# 2. 等就绪
docker compose ps              # loki 显示 healthy 后继续

# 3. 业务服务单独部署；确保 Serilog__WriteTo__1__Args__uri 指向 Loki 地址

# 4. 验证 Loki 收到日志
curl -G http://localhost:3100/loki/api/v1/query \
  --data-urlencode 'query={service="OpenAgent.Engine"}' \
  --data-urlencode "time=$(date +%s)"

# 5. 打开 Grafana
open http://localhost:3000    # admin / $GRAFANA_ADMIN_PASSWORD
# Explore → Loki → 输入 {service="OpenAgent.Engine"}
```

---

## 六、被否决的方案与原因

### 6.1 Seq 方案

Seq 是 .NET 生态中最有"原生体验"优势的日志服务端，曾被作为候选方案之一。**本次决策正式退场**，原因：

| 维度 | Seq | Loki + Grafana |
|---|---|---|
| 许可证 | 商业软件，免费版**限 5GB / 单用户**，超量需付费 | 完全开源免费 |
| 长期成本 | 商业项目有规模后必付费 | 零授权费 |
| 多服务聚合查询 | 中（单实例，难跨服务） | 强（标签模型 + Grafana 多数据源） |
| 与 OTel 生态打通 | 支持 OTLP，但生态有限 | 原生 Grafana + Tempo + Prometheus 全家桶 |
| AI 接入 | 官方 MCP Server | 自研 MCP + HTTP API（与后端解耦） |
| 容器化 / K8s | 一般 | ⭐⭐⭐⭐⭐ |

> **不是 Seq 不好**——而是**商业项目**要求**零授权风险 + 跨服务聚合**两大能力，Seq 在这两点上弱于 Loki。

### 6.2 ELK（Elastic 官方 Elasticsearch + Kibana）

- ❌ 2021 起 Elasticsearch 改 **ELv2 / SSPL** 双协议
- ⚠️ 资源消耗高，运维复杂
- ✅ 仅在需要**强全文检索 + 复杂聚合 + 商业付费意愿**时考虑

### 6.3 OpenSearch

- ✅ Apache 2.0，许可证最干净
- ✅ API 兼容 ES 7.10，迁移成本低
- ⏸️ **当前不引入**——Loki 标签模型更契合多服务场景
- 备选保留：日志量 / 全文检索需求出现时再迁移
- 迁移路径已留好（Serilog 多 Sink + Grafana 多数据源）

---

## 七、扩展路线图

### 7.1 当前阶段（已决策）

```
Serilog 直推 Loki + Grafana
└─ 业务零改动
└─ 30 分钟内可上线
└─ 2 个新容器
```

### 7.2 近期（≤ 3 个月）

| 工作项 | 优先级 | 复杂度 |
|---|---|---|
| `Agent.Hosting` 封装 `UseAgentSerilog()` 扩展 | P0 | 低 |
| Grafana 配 Service / Instance / Tenant 维度面板 | P0 | 低 |
| Loki retention 30 天 + S3/MinIO 生命周期双保险 | P0 | 低 |
| 敏感字段（Authorization / Cookie / ConnectionString）脱敏 Enricher | P1 | 中，本期暂不做 |
| 接入 Tempo（OTel Traces），Logs ↔ Traces 联动 | P1 | 中 |

### 7.3 中期（3–6 个月）

| 工作项 | 触发条件 |
|---|---|
| 引入 Promtail，采集非 .NET 服务的 stdout | 出现 Python/Java 服务 |
| 自研 `ILogQueryService`（封装 Loki HTTP API） | AI 接入需求出现 |
| 自研 MCP Server（包装 `ILogQueryService`） | Agent 工具化需求 |
| OpenLLMetry 接入，把 LLM 调用 token/prompt/response 入日志 | 启用 `Microsoft.Extensions.AI` 后 |

### 7.4 远期（6+ 个月）

| 工作项 | 触发条件 |
|---|---|
| 评估 OpenSearch 迁移 | 日志量 > 1TB/日 OR 需要复杂全文检索 |
| 评估 Apache 2.0 全栈替换 Grafana | 公司策略要求彻底规避 AGPL |
| 接入 Prometheus / 长期 Metrics 存储 | 业务对指标有长期趋势分析需求 |
| 异常检测 / 智能告警 | Loki ruler / Grafana ML |

---

## 八、关键风险与监控

### 8.1 必须监控的指标

```promql
# Loki 写入速率
rate(loki_distributor_lines_received_total[5m])

# Loki 写入失败
rate(loki_distributor_dropped_lines_total[5m])

# Compactor 运行状态
time() - loki_compactor_last_compaction_run_timestamp_seconds

# Serilog 缓冲丢弃（应用侧）
# 通过 Serilog 自定义指标暴露
```

### 8.2 后续补充：敏感字段脱敏

| 字段 | 处理方式 |
|---|---|
| `Authorization` | Enricher 替换为 `***` |
| `Cookie` / `Set-Cookie` | Enricher 替换为 `***` |
| `ConnectionString` | Enricher 替换为 `***` |
| `password` / `secret` / `apiKey` 模板变量 | Enricher 替换为 `***` |

> 当前阶段先不实现脱敏；上线到更严格生产环境前再补。原因是 Loki 写入后**几乎无法彻底删除**（虽有 delete_request API），最终仍建议在 Enricher 层提前防住。

### 8.3 容量规划

| 日志量 | Loki 节点 | 磁盘 | 备注 |
|---|---|---|---|
| 10 GB/日 | 1 节点 2C4G | 500 GB | 单机可撑 |
| 100 GB/日 | 3 节点 4C8G | 2 TB | 集群模式 |
| 1 TB/日 | 5+ 节点 + MinIO | 10 TB+ | Loki 进入分布式模式，监控 compactor 队列 |

---

## 九、给团队的具体行动清单

### 9.1 本周（必须完成）

- [x] 在 `Agent.Hosting.csproj` 添加 Serilog / Console Sink / Loki Sink / Enricher / Configuration 相关包
- [x] 新增独立 Loki + Grafana compose 配置：`deploy/observability/docker-compose.yml`
- [x] 各服务 `Program.cs` 加 `UseAgentSerilog(...)`
- [x] `appsettings.json` 配 Serilog 节 + Loki URL
- [x] Grafana 配 Loki 数据源
- [ ] 确认业务服务实际部署环境里的 Loki 地址，并设置 `Serilog__WriteTo__1__Args__uri`
- [ ] 未来按需补 `level` label：做 Grafana 错误面板或告警时，再通过 Enricher 或 Promtail pipeline 提升日志级别

### 9.2 下周（建议完成）

- [x] 将 Serilog 初始化收口成统一 Host 扩展，减少新 Host 漏接风险
- [ ] 敏感字段脱敏 Enricher（本期暂不做，生产化前补）
- [x] Loki retention / compactor 开启
- [ ] 压测写入速率（确认 ingest_limit 合理）

### 9.3 本月（可选完成）

- [ ] Promtail 部署就位（备用）
- [ ] Tempo 接入（链路追踪）
- [ ] 自研 `ILogQueryService`（为 AI 接入做准备）

---

## 十、总结

**最终选型**：**Serilog（应用侧）+ Loki（存储）+ Grafana（可视化）**，**未来按需扩展 Promtail**。

**核心收益**：
1. **业务代码零改动**——MEL 抽象 + Serilog 桥接，67 处 `ILogger<T>` 调用照常工作
2. **零商业授权风险**——所有组件在内部自用场景下均合规
3. **低资源、高扩展**——单机 2C4G 起，云原生友好
4. **未来可平滑升级**——Promtail（多语言）、Tempo（链路）随时可叠加；如需全文检索，可换 Apache 2.0 后端

**核心约束**：
- 标签基数必须低（业务字段放 properties）
- 敏感字段脱敏本期暂不做，生产化前应在 Enricher 层补齐
- AGPL 仅限内部运维自用，不二次开发 Grafana 后对外发布

**核心成本**：
- 改造工时 ≈ 3 小时
- 新增 2 个容器（Loki + Grafana）
- 长期零授权费

---

## 十一、附录：Enrich 层是什么

Enrich 是 Serilog 流水线中位于"日志产生"和"Sink 输出"之间的**字段补全环节**。日志在业务代码里被调用时往往只有一个 `LogInformation("User {UserId} login", userId)`，而 Loki / Grafana 真正想看的是 `{service="OpenAgent.Engine", instance="engine-3", level="Info", env="prod"}`——这些字段不在业务调用里，也不在 `appsettings.json` 里，**必须在日志被发出去之前动态注入**。这就是 Enrich 层的工作。

### 11.1 它在流水线中的位置

```
业务代码: _logger.LogInfo("User {UserId} login", 123)
                  │
                  ▼
       ┌──────────────────┐
       │  LogEvent 生成    │  ← 模板解析，{UserId} → 123
       └────────┬─────────┘
                │
                ▼
       ┌──────────────────┐
       │  Enricher 链      │  ← 在这里补 service / instance / level / traceId
       └────────┬─────────┘
                │
                ▼
       ┌──────────────────┐
       │  Filter (可选)    │  ← 按 level / property 过滤
       └────────┬─────────┘
                │
                ▼
       ┌──────────────────┐
       │  Sink 写出        │  ← Console / Loki / File / ...
       └──────────────────┘
```

### 11.2 常用 Enricher 与作用

| Enricher | 注入的字段 | 作用 |
|---|---|---|
| `WithMachineName` | `MachineName` | 多实例场景定位到具体主机 |
| `WithProcessId` / `WithThreadId` | `ProcessId` / `ThreadId` | 排查并发问题 |
| `WithEnvironmentName` | `Environment` | 区分 dev / staging / prod |
| `WithProperty("ServiceName", ...)` | 任意业务字段 | 把 `ServiceName` 注入每条日志 |
| `FromLogContext` | `LogContext.PushProperty(...)` 的字段 | **最关键**——业务代码里用 `using (LogContext.PushProperty("tenantId", id))` 临时注入 |
| `WithCorrelationId` | `CorrelationId` | 一次 HTTP 调用链共享一个 ID |
| 自定义 `Enricher` | 任意字段 | 比如从 DI 容器读当前用户 ID 注入 |

### 11.3 `FromLogContext` 是最常用的模式

业务代码里这样用：

```csharp
using (LogContext.PushProperty("tenantId", tenantId))
using (LogContext.PushProperty("conversationId", conversationId))
{
    _logger.LogInformation("Processing request for {UserId}", userId);
}
```

日志产生后，Enricher 会**自动**把当前 `LogContext` 栈里的字段附到这条 LogEvent 上，再交给 Sink。

### 11.4 Enrich vs Sink 的职责边界

| 关注点 | Enricher | Sink |
|---|---|---|
| **改字段**（补字段、改值） | ✅ 全部在这里 | ❌ 不改 |
| **决定输出到哪里** | ❌ 不管 | ✅ 全部在这里 |
| **格式 / 编码** | ❌ | ✅（Loki JSON / Console 文本 / File 自定义） |
| **异步 / 批量 / 重试** | ❌ | ✅ |
| **可加多个？** | ✅（链式） | ✅（广播） |

> **Enrich 决定"日志带什么字段"，Sink 决定"日志送到哪里、怎么送"**。两者完全解耦，**所以 §11.2 的脱敏 Enricher 才是 Loki 写入前唯一可拦的地方**（见 §8.2）。

### 11.5 标签 vs Properties 的区分

Loki 场景下，Enrich 注入的字段会变成两种形态：

- **Loki Label**：Loki 索引中可被 `LogQL {key="value"}` 直接过滤；基数必须低
- **Properties**：随日志原文一起存，**不能**被 LogQL label 过滤，只能在内容里被 `|= "text"` 搜索；基数无限制

通过 `Serilog.Sinks.Grafana.Loki` 的两个配置项控制：

```json
{
  "labels": [
    { "key": "service", "value": "OpenAgent.Engine" }
  ]
}
```

- `labels`：**静态**提升为 Loki label（适合 service、env 这种固定值）
- `propertiesAsLabels`：**动态**从日志字段提升为 label（适合 level 之类）
- 其它字段默认进 properties（适合 tenantId、conversationId）

> 当前配置只使用 `labels`，暂不配置 `propertiesAsLabels`。

> 关于 `level` 的直白说明：`labels` 像是给每条日志贴固定贴纸，比如 `service=OpenAgent.Engine`；`propertiesAsLabels` 是“如果日志内容里已经有某个字段，就把它也贴成贴纸”。Serilog 的日志级别本来是日志事件自己的等级，不一定天然存在一个名叫 `level` 的普通字段。本期先不把 `level` 提升为 Loki label；后续需要 Grafana 错误面板、按级别筛选或告警时，再补一个 Enricher 写入 `level=Information/Warning/Error/...`，或者在 Promtail 采集 stdout 时用 pipeline 解析并提升。

> ⚠️ 把 `tenantId` 误放进 `propertiesAsLabels`，租户超过几百个就会**撑爆 Loki 索引**。这正是 §5.3 表格"提升为 label？慎用"那一栏存在的理由。

### 11.6 在 OpenAgent 里的统一约定

`Agent.Hosting` 的 `UseAgentSerilog()` 扩展里固定注入这些字段，**业务层零感知**：

```csharp
// 统一 Enrich 配置（在 UseAgentSerilog 里集中放）
.Enrich.FromLogContext()
.Enrich.WithMachineName()
.Enrich.WithThreadId()
.Enrich.WithProperty("ServiceName", options.ServiceName)
.Enrich.WithProperty("InstanceId", Environment.MachineName)
```

业务代码里用 `LogContext.PushProperty("tenantId", ...)` 临时注入，**不写在 Enrich 扩展里**——因为 Enrich 是进程级常量，LogContext 才是请求级动态字段。
