# 平台健康检查（Platform Health Check）设计

日期：2026-08-12
状态：Approved

## 背景

当前"工作台设置 → 诊断"Tab（`Frontend/OpenAgent.Chat/src/App.vue`）是纯前端实现，仅做 5 项 HTTP 探测（`/health`、`/ready` 只判 200，不解析明细）。目标是重构为**平台健康检查**：统一后端健康检查、支持逐项明细展示，覆盖 Redis、数据库、Engine 连接、文件存储等平台基础设施。

## 目标 / 非目标

**目标**
- 将"工作台诊断"重命名为"平台健康检查"，提供分组、明细、总体状态的健康面板。
- 后端以统一方式检测多个必要项（Redis、PostgreSQL、文件存储、LLM 配置就绪、Agent 配置缓存），返回逐项明细。
- Router 模式下直接探测 Engine 地址，拿到 Engine 侧基础设施真实状态。
- `OpenAgent.Infrastructure` 项目目录按功能划分（仅移动文件，命名空间不变）。

**非目标**
- 不在健康检查里发起 LLM 真实连接测试（已在"LLM 配置"Tab 提供单独测试）。
- 不做权限/租户维度的健康隔离，保持与 `/health` `/ready` 同级、公开可读。
- 不改动 Router 的 `/health` `/ready` 语义。

## 决策记录

1. **后端统一改动可接受**：新增 DB 健康检查 + 一个统一明细端点。
2. **Router 模式直连 Engine 地址**：用设置里的 Engine 地址从浏览器直连 `/health/report`；不可达时降级展示 Router 侧信息。
3. **LLM 不测真实连接**：健康检查仅展示 `llm-connectivity` 配置就绪状态。
4. **Infrastructure 重组选策略 A**：仅移动文件到功能目录，**命名空间保持不变**，零 EF 迁移影响。

## 一、后端改动（`OpenAgent.Engine.Host`）

### 1.1 新增 `DatabaseHealthCheck`

位置：`src/OpenAgent.Engine.Host/Health/DatabaseHealthCheck.cs`

- 注入 `IDbContextFactory<OpenAgentDbContext>`（来自 `OpenAgent.Infrastructure`，`Engine.Host` 已引用）。
- `CheckHealthAsync`：`CreateDbContextAsync` → `Database.CanConnectAsync()`，成功返回 `Healthy("Database is reachable")`，失败返回 `Unhealthy`。
- 注册为 `"database"`，tags `["infrastructure", "ready"]` → 自动并入标准 `/ready`。

> 依赖事实：`OpenAgent.Engine` 只引用 Contracts/Core，无法访问 `OpenAgentDbContext`；DB 检查必须放 `Engine.Host`。`OpenAgent.Architecture.Tests/AssemblyDependencyTests.cs` 的 `EngineHost_ReferencesOnlyApprovedLowerLayers` 已允许 `Engine.Host → Infrastructure`。

### 1.2 新增统一明细端点 `GET /health/report`

位置：`src/OpenAgent.Engine.Host/Health/HealthReportEndpointExtensions.cs`

- 公开、免鉴权（与 `/health` `/ready` 同级）。
- 调用 `IHealthCheckService.CheckHealthAsync()`（无谓词 = 运行全部已注册检查，未来新增检查自动纳入）。
- 返回归一化 JSON：

```json
{
  "status": "Healthy",
  "service": "agent-engine",
  "totalDurationMs": 8,
  "items": [
    { "key": "redis",              "status": "Healthy", "detail": "...", "latencyMs": 2, "data": {} },
    { "key": "database",           "status": "Healthy", "detail": "...", "latencyMs": 4, "data": {} },
    { "key": "agent-config",       "status": "Healthy", "detail": "...", "latencyMs": 3, "data": {} },
    { "key": "file-object-storage","status": "Healthy", "detail": "...", "latencyMs": 11, "data": {} }
  ]
}
```

- `items` 每项映射自 `HealthReport.Entries`：`key`=条目名，`status`=Healthy/Degraded/Unhealthy，`detail`=`Description`，`latencyMs`=`duration.TotalMilliseconds`，`data`=原始 `Data` 字典。
- 总体 `status` 映射自 `HealthReport.Status`；`service` 取 `IHostEnvironment.ApplicationName`。
- `file-object-storage` 仅在 `FileAssets.Enabled=true` 时注册并出现；`llm-connectivity` 顺带返回（仅配置就绪）。

### 1.3 后端测试

- `/health/report` 端点映射测试（参考 `HostingTests.UseAgentHost_MapsLegacyHealthCheckAliases` 模式）。
- `DatabaseHealthCheck` 单测：假 `IDbContextFactory<OpenAgentDbContext>` 包装真实 DbContext（`UseInMemoryDatabase`，测试项目需加 `Microsoft.EntityFrameworkCore.InMemory` 包）；Healthy 路径 `CanConnectAsync()` 返回 true，Unhealthy 路径 `CreateDbContextAsync()` 抛异常。（可参考 `RedisHealthCheckTests` 的 Fake 模式。）

## 二、`OpenAgent.Infrastructure` 目录按功能划分（策略 A：仅移动，命名空间不变）

**目标结构**（所有类型命名空间保持不变，零 EF 迁移影响）：

```
src/OpenAgent.Infrastructure/
├── ServiceCollectionExtensions.cs          # OpenAgent.Infrastructure      （DI 装配，留在根）
├── Conversations/                          # namespace OpenAgent.Infrastructure 不变
│   ├── EfCoreConversationStore.cs
│   ├── WriteThroughConversationStore.cs
│   ├── RedisConversationCache.cs
│   └── RedisConversationLock.cs
├── FileAssets/                             # namespace OpenAgent.Infrastructure 不变
│   └── EfCoreFileAssetRepository.cs
├── Persistence/                            # namespace OpenAgent.Infrastructure 不变
│   ├── OpenAgentDbContext.cs
│   ├── OpenAgentDbContextFactory.cs
│   └── Migrations/                         # namespace OpenAgent.Infrastructure.Migrations 不变
│       ├── 20260811092634_InitialOpenAgentPostgres.cs
│       ├── 20260811092634_InitialOpenAgentPostgres.Designer.cs
│       └── OpenAgentDbContextModelSnapshot.cs
└── Entities/                               # namespace OpenAgent.Infrastructure.Entities 不变
    ├── ConversationEntity.cs
    ├── ConversationMessageEntity.cs
    ├── ConversationFileReferenceEntity.cs
    ├── MessageFileReferenceEntity.cs
    └── FileAssetEntity.cs
```

**要点**
- 只移动文件路径，**不改任何命名空间** → `Migrations/` 快照与 Designer 里的实体类型名字符串无需改动。
- `Entities/` 保持在根（与 `.Entities` 命名空间一致），实体是共享数据模型，不按功能拆分。
- `ServiceCollectionExtensions.cs` 留在根命名空间，`Engine.Host/Program.cs` 的 `using OpenAgent.Infrastructure;` 不受影响。
- 验证：`dotnet build Backend/OpenAgent.sln` + `dotnet test`（Infrastructure.Tests 需要 Docker/Testcontainers；Architecture.Tests 不依赖文件夹）。

## 三、前端改动

### 3.1 `api.ts`

- 新增类型 `HealthReport` / `HealthReportItem`（status、detail、latencyMs、data 等）。
- 新增 `fetchHealthReport(baseUrl: string): Promise<HealthReport>`：`GET {baseUrl}/health/report`，解析 JSON。
- 新增 `fetchHealth(path, baseUrl?)`：显式指定 baseUrl 拉取 `/health` `/ready`（Router 模式直连 Engine 用）。

### 3.2 新组件 `src/components/HealthCheckPanel.vue`

从 `App.vue` 抽出诊断逻辑，独立组件（沿用现有组件化模式）。

- **总体状态横幅**：全 Healthy=绿"平台健康" / 有 Degraded=黄"部分降级" / 有 Unhealthy=红"存在异常"，含"运行全部"按钮。
- **服务连接组**：Engine 服务（直连 `/health/report` 结果）、Router 服务（Router 模式显示，探测活动端点 `/health`+`/ready`）、认证身份（`/me`）。
- **基础设施组**：Redis、PostgreSQL 数据库、文件存储（`file-object-storage` 缺失时显示"未启用"而非报错）。
- **数据与能力组**：Agent 目录（`/agents`）、会话存储（`/conversations`）、LLM 配置（informational，注明真实测试在 LLM 配置页）。
- **卡片**：状态点 + 名称 + 延迟 ms + detail，可展开查看原始 `data` 明细。
- **探测逻辑**：
  - 始终直连 `engineUrl` 的 `/health/report`。
  - Router 模式追加探测活动端点（router）的 `/health` `/ready`。
  - identity / catalog / conversations 走活动端点。
  - 并行运行，逐项显示延迟。
- **降级策略**：Engine 地址不可达（Docker DNS、鉴权失败）时，Engine 卡片显示"无法直连 + 原因"，并回退展示 Router 侧信息；基础设施项标记为未知/降级。

### 3.3 `App.vue`

- 删除旧 `DiagnosticKey` / `diagnostics` / `runDiagnostics` 相关逻辑。
- 诊断 Tab 替换为 `<HealthCheckPanel />`。
- Tab 标签"诊断"→"健康检查"；标题"工作台诊断"→"平台健康检查"；说明文案同步更新。
- 侧栏快捷按钮"运行工作台诊断"→"平台健康检查"。
- 打开面板自动运行：`activeSettings` 联合类型与 `openSettings` / `handleSettingsTabChange` 中的 `'diagnostics'` key 统一改名为 `'health'`。

## 四、错误处理与降级

| 场景 | 表现 |
|------|------|
| Engine `/health/report` 不可达 | Engine 卡片"无法直连"，基础设施项显示"未知"，回退 Router 侧信息 |
| `file-object-storage` 未注册 | 显示"未启用（FileAssets 未开启）"，不计为异常 |
| `llm-connectivity` Degraded/Unhealthy | 黄色/红色展示 + detail，标注真实测试在 LLM 配置页 |
| Redis 不可用（引擎孤岛模式） | `redis` 项 Degraded，总体状态黄 |
| 单项请求异常 | 该卡片 error + 错误信息，不影响其他项（并行隔离） |

## 五、测试策略

- **后端**：`DatabaseHealthCheck` 单测；`/health/report` 端点映射测试。
- **前端**：`api.test.ts` 增补 `fetchHealthReport` 解析用例（含 Degraded/Unhealthy、缺失 `file-object-storage` 场景）。
- **手工验证**：`dotnet build` + `dotnet test`；`pnpm` 前端构建；本地 compose 起栈后打开健康检查面板逐项核对。

## 六、范围外

- LLM 真实连接测试（保留在 LLM 配置 Tab）。
- Router 端健康检查语义改动。
- `OpenAgent.Infrastructure` 命名空间变更（策略 B，本次不做）。
- 生产级鉴权/权限策略。
