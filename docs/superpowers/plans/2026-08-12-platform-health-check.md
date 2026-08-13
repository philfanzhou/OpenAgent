# 平台健康检查 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将"工作台诊断"重构为"平台健康检查"：新增后端 DB 健康检查与统一明细端点 `/health/report`，`OpenAgent.Infrastructure` 目录按功能划分（仅移动文件），前端新增 HealthCheckPanel 组件并接入 App.vue。

**Architecture:** 后端在 `Engine.Host` 新增 `DatabaseHealthCheck`（tags `["infrastructure","ready"]`）并映射公开的 `GET /health/report`（内部跑全部已注册 IHealthCheck 返回归一化明细）。前端新增 `HealthCheckPanel.vue`，直连 Engine 的 `/health/report` 拿基础设施明细；Router 模式追加探测 Router `/health`+`/ready`。`OpenAgent.Infrastructure` 只移动文件路径，命名空间不变，零 EF 迁移影响。

**Tech Stack:** .NET 8 / ASP.NET Core HealthChecks / EF Core / Vue 3 + Element Plus / Vitest / xUnit。

## Global Constraints

- 文件作用域命名空间 `namespace X;`；注释与字符串一律英文；一个文件一个类。
- `OpenAgent.Engine` 只引用 Contracts/Core；`OpenAgent.Engine.Host` 可引用 Infrastructure（Architecture.Tests 已允许）。
- 所有新包版本固定于 `Backend/Directory.Packages.props`（禁止浮动版本）。
- 目录/命名空间：`OpenAgent.Infrastructure` 重组只移动文件，**禁止改命名空间**。
- 前端 UI 文案为中文；后端 C# 注释/日志为英文。
- 提交信息用 conventional commits（`feat`/`fix`/`chore`/`refactor`/`docs`）。
- 验证命令：`dotnet build Backend/OpenAgent.sln`；`dotnet test Backend/OpenAgent.sln`（Infrastructure.Tests 需 Docker）；前端 `cd Frontend/OpenAgent.Chat && pnpm type-check && pnpm test && pnpm build`。

---

## File Structure

**新增后端：**
- `Backend/src/OpenAgent.Engine.Host/Health/DatabaseHealthCheck.cs` — DB 连通性检查
- `Backend/src/OpenAgent.Engine.Host/Health/HealthReportEndpointExtensions.cs` — `/health/report` 端点
- `Backend/tests/OpenAgent.Engine.Tests/HealthChecks/DatabaseHealthCheckTests.cs` — DB 检查单测
- `Backend/tests/OpenAgent.Engine.Tests/Hosting/HealthReportEndpointTests.cs` — 端点映射测试

**修改后端：**
- `Backend/src/OpenAgent.Engine.Host/Program.cs` — 注册 DatabaseHealthCheck + MapHealthReport
- `Backend/Directory.Packages.props` — 加 `Microsoft.EntityFrameworkCore.InMemory`
- `Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj` — 引用 InMemory

**Infrastructure 目录移动（git mv，不改命名空间）：**
- `EfCoreConversationStore.cs` → `Conversations/`
- `WriteThroughConversationStore.cs` → `Conversations/`
- `RedisConversationCache.cs` → `Conversations/`
- `RedisConversationLock.cs` → `Conversations/`
- `EfCoreFileAssetRepository.cs` → `FileAssets/`
- `OpenAgentDbContext.cs` → `Persistence/`
- `OpenAgentDbContextFactory.cs` → `Persistence/`
- `Migrations/*` → `Persistence/Migrations/`

**新增前端：**
- `Frontend/OpenAgent.Chat/src/components/HealthCheckPanel.vue` — 健康检查面板

**修改前端：**
- `Frontend/OpenAgent.Chat/src/types.ts` — HealthReport 类型
- `Frontend/OpenAgent.Chat/src/api.ts` — `fetchHealthReport` / `fetchHealth`
- `Frontend/OpenAgent.Chat/src/api.test.ts` — 新 API 解析用例
- `Frontend/OpenAgent.Chat/src/workspace.css` — 面板样式
- `Frontend/OpenAgent.Chat/src/App.vue` — 替换诊断逻辑 + 改名

---

### Task 1: DatabaseHealthCheck（后端，TDD）

**Files:**
- Create: `Backend/src/OpenAgent.Engine.Host/Health/DatabaseHealthCheck.cs`
- Modify: `Backend/Directory.Packages.props`
- Modify: `Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj`
- Test: `Backend/tests/OpenAgent.Engine.Tests/HealthChecks/DatabaseHealthCheckTests.cs`

**Interfaces:**
- Consumes: `OpenAgentDbContext(DbContextOptions<OpenAgentDbContext>)`（主构造函数），`IDbContextFactory<OpenAgentDbContext>`（Microsoft.EntityFrameworkCore）
- Produces: `internal sealed class DatabaseHealthCheck : IHealthCheck`，`CheckHealthAsync(HealthCheckContext, CancellationToken)` 返回 `Task<HealthCheckResult>`（Healthy "Database is reachable" / Unhealthy）

- [ ] **Step 1: 声明 InMemory 包版本并引用到测试项目**

在 `Backend/Directory.Packages.props` 的 `<!-- 基础设施 -->` 块末尾追加：

```xml
    <PackageVersion Include="Microsoft.EntityFrameworkCore.InMemory" Version="8.0.11" />
```

在 `Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj` 的 `<ItemGroup>` 追加：

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" />
  </ItemGroup>
```

- [ ] **Step 2: 写失败测试**

创建 `Backend/tests/OpenAgent.Engine.Tests/HealthChecks/DatabaseHealthCheckTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Engine.Host.Health;
using OpenAgent.Infrastructure;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class DatabaseHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_DatabaseReachable_ReturnsHealthy()
    {
        var options = new DbContextOptionsBuilder<OpenAgentDbContext>()
            .UseInMemoryDatabase("health-test")
            .Options;
        var check = new DatabaseHealthCheck(new InMemoryContextFactory(options));

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_DatabaseUnreachable_ReturnsUnhealthy()
    {
        var check = new DatabaseHealthCheck(new ThrowingContextFactory());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class InMemoryContextFactory(IDbContextFactory<OpenAgentDbContext> inner) : IDbContextFactory<OpenAgentDbContext>
    {
        public OpenAgentDbContext CreateDbContext() => inner.CreateDbContext();

        public Task<OpenAgentDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class ThrowingContextFactory : IDbContextFactory<OpenAgentDbContext>
    {
        public OpenAgentDbContext CreateDbContext() => throw new InvalidOperationException("no database");

        public Task<OpenAgentDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no database");
    }
}
```

> 注：`InMemoryContextFactory` 包装了一个真实 DbContext（`new OpenAgentDbContext(options)`）。若 `CanConnectAsync` 对 InMemory provider 有兼容问题，在 Task 1 验证时改为 `Database.CanConnect()` 同步调用并调整实现（见 Step 4 的探测语句）。

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj --filter DatabaseHealthCheckTests`
Expected: 编译失败，`DatabaseHealthCheck` 类型不存在。

- [ ] **Step 4: 实现 DatabaseHealthCheck**

创建 `Backend/src/OpenAgent.Engine.Host/Health/DatabaseHealthCheck.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Infrastructure;

namespace OpenAgent.Engine.Host.Health;

internal sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<OpenAgentDbContext> _contexts;

    public DatabaseHealthCheck(IDbContextFactory<OpenAgentDbContext> contexts)
    {
        _contexts = contexts;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using OpenAgentDbContext database = await _contexts.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            bool connected = await database.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
            return connected
                ? HealthCheckResult.Healthy("Database is reachable")
                : HealthCheckResult.Unhealthy("Database is not reachable");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Database connection failed", exception);
        }
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj --filter DatabaseHealthCheckTests`
Expected: PASS（2 个用例）。

- [ ] **Step 6: 注册到 Program.cs 并提交**

在 `Backend/src/OpenAgent.Engine.Host/Program.cs` 的 `builder.Services.AddAgentEngine(...)` 之后追加：

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["infrastructure", "ready"]);
```

`Program.cs` 顶部追加 `using OpenAgent.Engine.Host.Health;`。

提交：

```bash
git add Backend/Directory.Packages.props Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj Backend/src/OpenAgent.Engine.Host/Health/DatabaseHealthCheck.cs Backend/tests/OpenAgent.Engine.Tests/HealthChecks/DatabaseHealthCheckTests.cs Backend/src/OpenAgent.Engine.Host/Program.cs
git commit -m "feat(health): add PostgreSQL database health check"
```

---

### Task 2: `/health/report` 统一明细端点（后端，TDD）

**Files:**
- Create: `Backend/src/OpenAgent.Engine.Host/Health/HealthReportEndpointExtensions.cs`
- Create: `Backend/tests/OpenAgent.Engine.Tests/Hosting/HealthReportEndpointTests.cs`
- Modify: `Backend/src/OpenAgent.Engine.Host/Program.cs`

**Interfaces:**
- Consumes: `IHealthCheckService.CheckHealthAsync(predicate: null, cancellationToken)` → `HealthReport`（`Status`/`TotalDuration`/`Entries`：`Description`/`Duration`/`Data`/`Status`）
- Produces: `internal static class HealthReportEndpointExtensions` 的 `MapHealthReport(this IEndpointRouteBuilder)`，映射 `GET /health/report`，返回 `{ status, service, totalDurationMs, items[] }`（items: `{ key, status, detail, latencyMs, data }`）

- [ ] **Step 1: 写失败测试**

创建 `Backend/tests/OpenAgent.Engine.Tests/Hosting/HealthReportEndpointTests.cs`：

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Engine.Host.Health;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class HealthReportEndpointTests
{
    [Fact]
    public void MapHealthReport_MapsReportEndpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddHealthChecks()
            .AddCheck("redis", () => HealthCheckResult.Healthy(), tags: ["live", "ready"]);

        var app = builder.Build();
        app.MapHealthReport();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/health/report", routePatterns);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj --filter HealthReportEndpointTests`
Expected: 编译失败，`MapHealthReport` 不存在。

- [ ] **Step 3: 实现端点**

创建 `Backend/src/OpenAgent.Engine.Host/Health/HealthReportEndpointExtensions.cs`：

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace OpenAgent.Engine.Host.Health;

internal static class HealthReportEndpointExtensions
{
    public static IEndpointConventionBuilder MapHealthReport(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/health/report", async (
            IHealthCheckService service,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            HealthReport report = await service.CheckHealthAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                status = report.Status.ToString(),
                service = environment.ApplicationName,
                totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds),
                items = report.Entries.Select(entry => new
                {
                    key = entry.Key,
                    status = entry.Value.Status.ToString(),
                    detail = entry.Value.Description,
                    latencyMs = Math.Round(entry.Value.Duration.TotalMilliseconds),
                    data = entry.Value.Data
                })
            });
        });
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj --filter HealthReportEndpointTests`
Expected: PASS。

- [ ] **Step 5: 接入 Program.cs 并提交**

在 `Backend/src/OpenAgent.Engine.Host/Program.cs` 的 `app.MapAgentEndpoints();` 之后追加：

```csharp
app.MapHealthReport();
```

`Program.cs` 顶部已含 `using OpenAgent.Engine.Host.Health;`（Task 1 已加）。

提交：

```bash
git add Backend/src/OpenAgent.Engine.Host/Health/HealthReportEndpointExtensions.cs Backend/tests/OpenAgent.Engine.Tests/Hosting/HealthReportEndpointTests.cs Backend/src/OpenAgent.Engine.Host/Program.cs
git commit -m "feat(health): add unified health report endpoint"
```

---

### Task 3: `OpenAgent.Infrastructure` 目录按功能划分（仅移动文件）

**Files:**
- Move（git mv，不改命名空间）：
  - `Backend/src/OpenAgent.Infrastructure/EfCoreConversationStore.cs` → `Conversations/`
  - `Backend/src/OpenAgent.Infrastructure/WriteThroughConversationStore.cs` → `Conversations/`
  - `Backend/src/OpenAgent.Infrastructure/RedisConversationCache.cs` → `Conversations/`
  - `Backend/src/OpenAgent.Infrastructure/RedisConversationLock.cs` → `Conversations/`
  - `Backend/src/OpenAgent.Infrastructure/EfCoreFileAssetRepository.cs` → `FileAssets/`
  - `Backend/src/OpenAgent.Infrastructure/OpenAgentDbContext.cs` → `Persistence/`
  - `Backend/src/OpenAgent.Infrastructure/OpenAgentDbContextFactory.cs` → `Persistence/`
  - `Backend/src/OpenAgent.Infrastructure/Migrations/*` → `Persistence/Migrations/`

**Interfaces:**
- 无（纯文件移动，所有类型与命名空间不变；`ServiceCollectionExtensions.cs` 与 `Entities/` 保持在根）。

- [ ] **Step 1: 执行 git mv**

在仓库根执行：

```bash
git mv Backend/src/OpenAgent.Infrastructure/EfCoreConversationStore.cs Backend/src/OpenAgent.Infrastructure/Conversations/EfCoreConversationStore.cs
git mv Backend/src/OpenAgent.Infrastructure/WriteThroughConversationStore.cs Backend/src/OpenAgent.Infrastructure/Conversations/WriteThroughConversationStore.cs
git mv Backend/src/OpenAgent.Infrastructure/RedisConversationCache.cs Backend/src/OpenAgent.Infrastructure/Conversations/RedisConversationCache.cs
git mv Backend/src/OpenAgent.Infrastructure/RedisConversationLock.cs Backend/src/OpenAgent.Infrastructure/Conversations/RedisConversationLock.cs
git mv Backend/src/OpenAgent.Infrastructure/EfCoreFileAssetRepository.cs Backend/src/OpenAgent.Infrastructure/FileAssets/EfCoreFileAssetRepository.cs
git mv Backend/src/OpenAgent.Infrastructure/OpenAgentDbContext.cs Backend/src/OpenAgent.Infrastructure/Persistence/OpenAgentDbContext.cs
git mv Backend/src/OpenAgent.Infrastructure/OpenAgentDbContextFactory.cs Backend/src/OpenAgent.Infrastructure/Persistence/OpenAgentDbContextFactory.cs
git mv Backend/src/OpenAgent.Infrastructure/Migrations Backend/src/OpenAgent.Infrastructure/Persistence/Migrations
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build Backend/OpenAgent.sln`
Expected: Build succeeded（0 error）。命名空间未变，无需改任何 using。

- [ ] **Step 3: 运行架构测试**

Run: `dotnet test Backend/tests/OpenAgent.Architecture.Tests --no-build`
Expected: PASS（依赖方向未变）。

- [ ] **Step 4: 提交**

```bash
git add -A Backend/src/OpenAgent.Infrastructure
git commit -m "refactor(infrastructure): organize project folders by feature"
```

---

### Task 4: 前端 api 层（types + fetchHealthReport + fetchHealth）

**Files:**
- Modify: `Frontend/OpenAgent.Chat/src/types.ts`
- Modify: `Frontend/OpenAgent.Chat/src/api.ts`
- Test: `Frontend/OpenAgent.Chat/src/api.test.ts`

**Interfaces:**
- Consumes: `normalizeBaseUrl`（api.ts 已有）、`headers`（已有）、`readError`（已有）
- Produces:
  - `interface HealthReportItem { key: string; status: 'Healthy' | 'Degraded' | 'Unhealthy'; detail?: string; latencyMs?: number; data?: Record<string, unknown> }`
  - `interface HealthReport { status: 'Healthy' | 'Degraded' | 'Unhealthy'; service?: string; totalDurationMs?: number; items: HealthReportItem[] }`
  - `interface HealthEntry { status: string; description?: string; duration?: string; data?: Record<string, unknown> }`
  - `interface NativeHealthReport { status: string; entries: Record<string, HealthEntry>; totalDuration?: string }`
  - `export async function fetchHealthReport(baseUrl: string): Promise<HealthReport>`
  - `export async function fetchHealth(baseUrl: string, path: '/health' | '/ready'): Promise<NativeHealthReport>`

- [ ] **Step 1: 在 types.ts 追加类型**

在 `Frontend/OpenAgent.Chat/src/types.ts` 末尾追加：

```ts
export interface HealthReportItem {
  key: string
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  detail?: string
  latencyMs?: number
  data?: Record<string, unknown>
}

export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  service?: string
  totalDurationMs?: number
  items: HealthReportItem[]
}

export interface HealthEntry {
  status: string
  description?: string
  duration?: string
  data?: Record<string, unknown>
}

export interface NativeHealthReport {
  status: string
  entries: Record<string, HealthEntry>
  totalDuration?: string
}
```

- [ ] **Step 2: 在 api.ts 追加函数**

在 `Frontend/OpenAgent.Chat/src/api.ts` 的 `request` 函数之后、`api` 对象之前追加：

```ts
export async function fetchHealthReport(baseUrl: string): Promise<HealthReport> {
  const response = await fetch(`${normalizeBaseUrl(baseUrl)}/health/report`, { headers: headers() })
  if (!response.ok) throw await readError(response)
  return await response.json() as HealthReport
}

export async function fetchHealth(baseUrl: string, path: '/health' | '/ready'): Promise<NativeHealthReport> {
  const response = await fetch(`${normalizeBaseUrl(baseUrl)}${path}`, { headers: headers() })
  if (!response.ok) throw await readError(response)
  return await response.json() as NativeHealthReport
}
```

在 `import type { ... } from './types'` 中追加 `HealthEntry, HealthReport, HealthReportItem, NativeHealthReport`。

- [ ] **Step 3: 写测试**

在 `Frontend/OpenAgent.Chat/src/api.test.ts` 的 import 中追加 `fetchHealthReport`，并新增用例：

```ts
it('parses the engine health report into typed items', async () => {
  const report = {
    status: 'Healthy',
    service: 'agent-engine',
    totalDurationMs: 8,
    items: [
      { key: 'redis', status: 'Healthy', detail: 'Redis connection is healthy', latencyMs: 2, data: {} },
      { key: 'database', status: 'Healthy', detail: 'Database is reachable', latencyMs: 4, data: {} },
    ],
  }
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(report), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })))

  const result = await fetchHealthReport('http://engine.example/')

  expect(result.status).toBe('Healthy')
  expect(result.items).toHaveLength(2)
  expect(result.items[0].key).toBe('redis')
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe('http://engine.example/health/report')
})
```

- [ ] **Step 4: 运行测试**

Run: `cd Frontend/OpenAgent.Chat && pnpm test`
Expected: 全部通过（含新增用例）。

- [ ] **Step 5: 提交**

```bash
git add Frontend/OpenAgent.Chat/src/types.ts Frontend/OpenAgent.Chat/src/api.ts Frontend/OpenAgent.Chat/src/api.test.ts
git commit -m "feat(workspace): add health report API client"
```

---

### Task 5: HealthCheckPanel 组件

**Files:**
- Create: `Frontend/OpenAgent.Chat/src/components/HealthCheckPanel.vue`
- Modify: `Frontend/OpenAgent.Chat/src/workspace.css`

**Interfaces:**
- Consumes: `fetchHealthReport(baseUrl)`、`fetchHealth(baseUrl, path)`、`api.getCurrentUser()`、`api.listAgents()`、`api.listConversations()`、`getConnectionMode()`、`getEngineBaseUrl()`、`getRouterBaseUrl()`
- Produces: `defineExpose({ run })`（App.vue 在 Tab 激活时调用 `healthPanel.value?.run()`）

- [ ] **Step 1: 创建组件**

创建 `Frontend/OpenAgent.Chat/src/components/HealthCheckPanel.vue`：

```vue
<script setup lang="ts">
import { computed, ref } from 'vue'
import { api, fetchHealth, fetchHealthReport, getConnectionMode, getEngineBaseUrl, getRouterBaseUrl } from '../api'
import type { HealthReport } from '../api'

type CheckStatus = 'idle' | 'running' | 'ok' | 'warn' | 'error' | 'na'
type CheckGroup = 'services' | 'infrastructure' | 'data'

interface CheckItem {
  key: string
  group: CheckGroup
  name: string
  detail: string
  status: CheckStatus
  latencyMs?: number
  data?: Record<string, unknown>
}

const mode = ref(getConnectionMode())
const engineUrl = ref(getEngineBaseUrl())
const routerUrl = ref(getRouterBaseUrl())
const running = ref(false)
const checks = ref<CheckItem[]>([])
const expanded = ref<Record<string, boolean>>({})

const groupMeta: Record<CheckGroup, { label: string; eyebrow: string }> = {
  services: { label: '服务连接', eyebrow: 'SERVICES' },
  infrastructure: { label: '基础设施', eyebrow: 'INFRASTRUCTURE' },
  data: { label: '数据与能力', eyebrow: 'DATA & CAPABILITIES' },
}
const groupOrder: CheckGroup[] = ['services', 'infrastructure', 'data']

const overall = computed(() => {
  const done = checks.value.filter(item => item.status === 'ok' || item.status === 'warn' || item.status === 'error')
  if (!done.length) return { status: 'idle' as CheckStatus, label: '待检测' }
  if (done.some(item => item.status === 'error')) return { status: 'error', label: '存在异常' }
  if (done.some(item => item.status === 'warn')) return { status: 'warn', label: '部分降级' }
  return { status: 'ok', label: '平台健康' }
})

const summary = computed(() => {
  const count = (status: CheckStatus) => checks.value.filter(item => item.status === status).length
  return `${count('ok')} 项正常 · ${count('warn')} 项降级 · ${count('error')} 项异常`
})

function seeded(): CheckItem[] {
  const services: CheckItem[] = [
    { key: 'engine', group: 'services', name: 'Engine 服务', detail: '待检测', status: 'idle' },
    ...(mode.value === 'router'
      ? [{ key: 'router', group: 'services' as CheckGroup, name: 'Router 服务', detail: '待检测', status: 'idle' as CheckStatus }]
      : []),
    { key: 'identity', group: 'services', name: '认证身份', detail: '待检测', status: 'idle' },
  ]
  const infra: CheckItem[] = [
    { key: 'redis', group: 'infrastructure', name: 'Redis', detail: '待检测', status: 'idle' },
    { key: 'database', group: 'infrastructure', name: 'PostgreSQL', detail: '待检测', status: 'idle' },
    { key: 'file-storage', group: 'infrastructure', name: '文件存储', detail: '待检测', status: 'idle' },
  ]
  const data: CheckItem[] = [
    { key: 'catalog', group: 'data', name: 'Agent 目录', detail: '待检测', status: 'idle' },
    { key: 'conversations', group: 'data', name: '会话存储', detail: '待检测', status: 'idle' },
    { key: 'llm-config', group: 'data', name: 'LLM 配置', detail: '待检测', status: 'idle' },
  ]
  return [...services, ...infra, ...data]
}

function index(key: string): number {
  return checks.value.findIndex(item => item.key === key)
}

function patch(key: string, item: Partial<CheckItem>): void {
  const at = index(key)
  if (at >= 0) checks.value[at] = { ...checks.value[at], ...item }
}

function mapStatus(status?: string): CheckStatus {
  if (status === 'Healthy') return 'ok'
  if (status === 'Degraded') return 'warn'
  return 'error'
}

function toggle(key: string): void {
  expanded.value[key] = !expanded.value[key]
}

async function probeEngine(): Promise<void> {
  let report: HealthReport
  try {
    report = await fetchHealthReport(engineUrl.value)
  } catch (error) {
    patch('engine', { status: 'error', detail: error instanceof Error ? error.message : 'Engine 无法直连' })
    for (const key of ['redis', 'database', 'file-storage']) {
      patch(key, { status: 'na', detail: 'Engine 不可达，无法检测' })
    }
    patch('llm-config', { status: 'na', detail: 'Engine 不可达，无法检测' })
    return
  }
  patch('engine', { status: mapStatus(report.status), detail: `${engineUrl.value} · 总耗时 ${report.totalDurationMs ?? '—'} ms` })
  const known: Record<string, string> = { redis: 'redis', database: 'database', 'file-object-storage': 'file-storage' }
  for (const item of report.items) {
    const target = known[item.key]
    if (target) {
      patch(target, { status: mapStatus(item.status), detail: item.detail || '', latencyMs: item.latencyMs, data: item.data })
    }
  }
  if (!report.items.some(item => item.key === 'file-object-storage')) {
    patch('file-storage', { status: 'na', detail: '未启用（FileAssets.Enabled=false）' })
  }
  const llm = report.items.find(item => item.key === 'llm-connectivity')
  if (llm) {
    patch('llm-config', { status: mapStatus(llm.status), detail: `${llm.detail || ''} · 真实连接测试见 LLM 配置页`, latencyMs: llm.latencyMs, data: llm.data })
  } else {
    patch('llm-config', { status: 'na', detail: '未配置 LLM Provider' })
  }
}

function parseDuration(duration?: string): number | undefined {
  if (!duration) return undefined
  const match = /^([0-9]+):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?$/.exec(duration)
  if (!match) return undefined
  const ms = Number((match[4] || '').padEnd(3, '0'))
  return Math.round(Number(match[1]) * 3600000 + Number(match[2]) * 60000 + Number(match[3]) * 1000 + ms)
}

async function probeRouter(): Promise<void> {
  try {
    const ready = await fetchHealth(routerUrl.value, '/ready')
    const entry = ready.entries['router-ready']
    patch('router', {
      status: mapStatus(ready.status),
      detail: entry?.description || ready.status,
      latencyMs: parseDuration(entry?.duration),
      data: entry?.data,
    })
  } catch (error) {
    patch('router', { status: 'error', detail: error instanceof Error ? error.message : 'Router 不可达' })
  }
}

async function probeGateway(): Promise<void> {
  const attempts: Array<{ key: string; fn: () => Promise<string> }> = [
    {
      key: 'identity',
      fn: async () => {
        const user = await api.getCurrentUser()
        return `${user.userId} · ${user.tenantId || '无租户'} · ${user.isAuthenticated ? '已认证' : '未认证'}`
      },
    },
    { key: 'catalog', fn: async () => `${(await api.listAgents()).length} 个可见 Agent` },
    { key: 'conversations', fn: async () => `${(await api.listConversations()).length} 个会话` },
  ]
  await Promise.all(attempts.map(async attempt => {
    const startedAt = performance.now()
    try {
      const detail = await attempt.fn()
      patch(attempt.key, { status: 'ok', detail, latencyMs: Math.round(performance.now() - startedAt) })
    } catch (error) {
      patch(attempt.key, { status: 'error', detail: error instanceof Error ? error.message : '请求失败', latencyMs: Math.round(performance.now() - startedAt) })
    }
  }))
}

async function run(): Promise<void> {
  running.value = true
  checks.value = seeded().map(item => ({ ...item, status: 'running' }))
  const tasks = [probeEngine(), probeGateway()]
  if (mode.value === 'router') tasks.push(probeRouter())
  await Promise.all(tasks)
  running.value = false
}

defineExpose({ run })
</script>

<template>
  <div class="health-check">
    <div class="section-heading">
      <div><span class="eyebrow">SYSTEM CHECK</span><h3>平台健康检查</h3><p>从浏览器逐项验证 Engine、基础设施与数据面状态，结果可直接用于联调报告。</p></div>
      <el-button type="primary" :loading="running" @click="run">运行全部</el-button>
    </div>

    <div class="health-banner" :class="overall.status">
      <span class="health-banner-dot" />
      <div><strong>{{ overall.label }}</strong><small>{{ running ? '正在运行检测…' : summary }}</small></div>
    </div>

    <section v-for="group in groupOrder" :key="group" class="health-group">
      <div class="health-group-heading"><span class="eyebrow">{{ groupMeta[group].eyebrow }}</span><h4>{{ groupMeta[group].label }}</h4></div>
      <div class="diagnostic-grid">
        <article v-for="item in checks.filter(item => item.group === group)" :key="item.key" :class="['diagnostic-card', item.status]">
          <div><span class="diagnostic-dot" /><strong>{{ item.name }}</strong><small v-if="item.latencyMs !== undefined">{{ item.latencyMs }} ms</small></div>
          <p>{{ item.detail }}</p>
          <button v-if="item.data && Object.keys(item.data).length" class="health-detail-toggle" @click="toggle(item.key)">
            {{ expanded[item.key] ? '收起' : '明细' }}
          </button>
          <pre v-if="expanded[item.key] && item.data" class="health-detail">{{ JSON.stringify(item.data, null, 2) }}</pre>
        </article>
      </div>
    </section>
  </div>
</template>
```

- [ ] **Step 2: 追加样式**

在 `Frontend/OpenAgent.Chat/src/workspace.css` 的 `.diagnostic-card` 相关规则之后追加：

```css
.health-banner { display: flex; align-items: center; gap: 10px; margin-top: 12px; padding: 12px 14px; border: 1px solid var(--workspace-line); border-radius: 8px; background: var(--workspace-bg); }
.health-banner > div { display: flex; flex-direction: column; }
.health-banner strong { font-size: 14px; }
.health-banner small { color: var(--workspace-muted); font-size: 12px; }
.health-banner-dot { width: 10px; height: 10px; border-radius: 50%; }
.health-banner.ok .health-banner-dot { background: var(--workspace-green); }
.health-banner.warn .health-banner-dot { background: #d09a38; }
.health-banner.error .health-banner-dot { background: var(--workspace-danger); }
.health-banner.idle .health-banner-dot { background: #8a8a8a; }
.health-group { margin-top: 18px; }
.health-group-heading { display: flex; align-items: baseline; gap: 10px; margin-bottom: 8px; }
.health-group-heading .eyebrow { color: #8a8a8a; font-size: 11px; letter-spacing: 1px; }
.health-group-heading h4 { margin: 0; font-size: 14px; }
.diagnostic-card.warn .diagnostic-dot { background: #d09a38; }
.diagnostic-card.na { opacity: 0.72; }
.diagnostic-card.na .diagnostic-dot { background: #8a8a8a; }
.health-detail-toggle { margin-top: 6px; padding: 0; border: none; background: none; color: #409eff; font-size: 12px; cursor: pointer; }
.health-detail { margin: 8px 0 0; padding: 8px; border-radius: 6px; background: rgba(0, 0, 0, 0.04); font-size: 11px; line-height: 1.5; overflow: auto; max-height: 160px; white-space: pre-wrap; }
html[data-theme='dark'] .health-banner, html[data-theme='dark'] .health-detail { color: var(--workspace-text); background: var(--workspace-bg); }
```

- [ ] **Step 3: 类型检查**

Run: `cd Frontend/OpenAgent.Chat && pnpm type-check`
Expected: 通过（0 error）。

- [ ] **Step 4: 提交**

```bash
git add Frontend/OpenAgent.Chat/src/components/HealthCheckPanel.vue Frontend/OpenAgent.Chat/src/workspace.css
git commit -m "feat(workspace): add platform health check panel"
```

---

### Task 6: App.vue 接入与改名

**Files:**
- Modify: `Frontend/OpenAgent.Chat/src/App.vue`

**Interfaces:**
- Consumes: `HealthCheckPanel`（`defineExpose({ run })`）；移除旧 `diagnostics` 相关类型/逻辑
- Produces: 无（面板自包含）

- [ ] **Step 1: import 组件并声明 ref**

在 `App.vue` 的 import 区追加：

```ts
import HealthCheckPanel from './components/HealthCheckPanel.vue'
```

在 `const activeSettings = ref<...>` 之后追加：

```ts
const healthPanel = ref<InstanceType<typeof HealthCheckPanel> | null>(null)
```

- [ ] **Step 2: 改名 activeSettings 联合类型**

将第 17 行改为：

```ts
const activeSettings = ref<'gateway' | 'health' | 'llm' | 'agent' | 'mcp' | 'skill' | 'rag'>('gateway')
```

- [ ] **Step 3: 删除旧诊断状态与函数**

删除第 64–74 行（`DiagnosticKey`/`DiagnosticStatus`/`DiagnosticItem`/`diagnostics`/`runningDiagnostics`）与第 871–891 行的 `runDiagnostics` 函数。

- [ ] **Step 4: 更新 openSettings 与 handleSettingsTabChange**

将两处 `if (name === 'diagnostics') void runDiagnostics()` 替换为：

```ts
if (name === 'health') void healthPanel.value?.run()
```

（`openSettings` 与 `handleSettingsTabChange` 各一处；`openSettings` 内替换 `'diagnostics'` 分支，`handleSettingsTabChange` 内替换 `'diagnostics'` 分支。）

- [ ] **Step 5: 替换 Tab 面板**

将第 962–966 行的 `<el-tab-pane label="诊断" name="diagnostics">…` 整体替换为：

```html
        <el-tab-pane label="健康检查" name="health">
          <section class="settings-section">
            <HealthCheckPanel ref="healthPanel" />
          </section>
        </el-tab-pane>
```

- [ ] **Step 6: 更新快捷按钮文案**

将第 941 行改为：

```html
          <el-button class="diagnostics-shortcut" @click="openSettings('health')">运行平台健康检查</el-button>
```

- [ ] **Step 7: 校验**

Run: `cd Frontend/OpenAgent.Chat && pnpm type-check && pnpm test`
Expected: 通过（0 error；旧 diagnostics 用例若存在需一并清理——检查 App.vue 是否含诊断相关测试）。

- [ ] **Step 8: 提交**

```bash
git add Frontend/OpenAgent.Chat/src/App.vue
git commit -m "refactor(workspace): replace diagnostics tab with platform health check"
```

---

### Task 7: 全量验证

- [ ] **Step 1: 后端构建 + 测试**

Run: `dotnet build Backend/OpenAgent.sln`（Expected: 0 error）
Run: `dotnet test Backend/OpenAgent.sln`（Expected: 全部通过；Infrastructure.Tests 需 Docker）

- [ ] **Step 2: 前端全量**

Run: `cd Frontend/OpenAgent.Chat && pnpm check`（Expected: 通过）

- [ ] **Step 3: 提交收尾**

```bash
git add -A
git commit -m "chore(workspace): final verification" --allow-empty
```

---

## Self-Review

**1. Spec coverage 对照：**
- [x] DB 健康检查 → Task 1
- [x] `/health/report` 统一明细端点 → Task 2
- [x] Infrastructure 目录按功能划分（策略 A，仅移动）→ Task 3
- [x] 前端 api 层（fetchHealthReport/fetchHealth）→ Task 4
- [x] HealthCheckPanel 组件（分组/总体横幅/明细展开/降级）→ Task 5
- [x] App.vue 接入与改名（Tab/标题/快捷按钮）→ Task 6
- [x] 后端 + 前端测试 → 各 Task + Task 7

**2. Placeholder scan：** 无 TBD/TODO；每个代码步骤含完整代码。

**3. Type consistency：**
- `fetchHealthReport(baseUrl): Promise<HealthReport>`（Task 4）↔ `probeEngine` 使用（Task 5）一致。
- `fetchHealth(baseUrl, path)`（Task 4）↔ `probeRouter` 使用（Task 5）一致。
- `HealthCheckPanel` `defineExpose({ run })`（Task 5）↔ App.vue `healthPanel.value?.run()`（Task 6）一致。
- `activeSettings` 联合类型 `'health'`（Task 6）与 Tab name、`openSettings`/`handleSettingsTabChange` 分支一致。
- `DatabaseHealthCheck` 构造注入 `IDbContextFactory<OpenAgentDbContext>`（Task 1 实现）与测试一致。
