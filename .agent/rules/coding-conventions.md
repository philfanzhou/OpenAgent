# 编码规范 — AI 代码生成规则

以下规则适用于本仓库所有 C# 代码，AI 生成代码时必须遵守。

---

## 1. 总则

### 1.1 适用范围
- 解决方案内的所有 C# 项目（`.csproj`）
- 所有源代码文件（`.cs`）
- 所有测试项目
- 所有配置文件

### 1.2 核心原则
- **稳定性优先**：选择经过生产环境验证的技术栈
- **最小依赖**：在满足功能需求的前提下，使用最低可行的 .NET 版本
- **兼容性保障**：确保组件具有良好的跨版本兼容性
- **可维护性**：代码风格统一，易于理解和维护
- **避免过期技术**：不使用已停止支持或过期的 .NET 版本

---

## 2. 项目分层规则（硬性约束，不可违反）

```
Contracts ← Core ← Engine/Router ← Host
```

- **Agent.Contracts**：纯接口、DTO、错误码。**不引用任何其他项目**
- **Agent.Core**：核心逻辑。可引用 Contracts，**不可**引用 Engine 或 Router
- **Agent.Engine / Agent.Router**：可引用 Contracts、Core。**不可**引用 Host
- **Agent.Hosting**：基础设施。可引用所有下层

> 新增项目引用前，先确认不违反上述依赖方向。

---

## 3. .NET 版本约束

### 3.1 最高版本限制

**本解决方案支持的最高 .NET 版本为 .NET 8.0。**

| 优先级 | 目标框架 | 适用场景 |
|--------|----------|----------|
| 1 (最佳) | .NET Standard 2.0/2.1 | 纯类库，不依赖 ASP.NET Core |
| 2 (可接受) | .NET 8.0 | Web API 项目、ASP.NET Core 应用 |
| 禁止 | .NET 6.0 及更早版本 | 已过期，不再接收安全更新 |

**理由：**
- .NET 8.0 是长期支持（LTS）版本，在生产环境中具有 proven 稳定性
- .NET 6.0 已于 2024 年 11 月结束支持，不再接收安全更新和补丁
- 使用过期版本存在安全风险，不符合企业级应用的安全合规要求

### 3.2 决策标准

**使用 .NET Standard 的场景：**
- 项目是纯类库（Class Library）
- 不依赖 ASP.NET Core 托管特定的 API
- 需要最大化的跨平台和跨版本兼容性

**使用 .NET 8.0 的场景：**
- Web API 项目（ASP.NET Core）
- 需要 ASP.NET Core 特定功能
- 控制台应用、Windows 服务等可执行项目

### 3.3 各模块当前 TFM

| 项目 | TFM | 类型 |
|------|-----|------|
| `Agent.Contracts` | net8.0 | 类库 |
| `Agent.Core` | net8.0 | 类库 |
| `OpenAgent.Engine` | net8.0 | 类库 |
| `OpenAgent.Core.Engine.Host` | net8.0 | Web API |
| `Agent.Router` | net8.0 | Web API |
| `Agent.Hosting` | net8.0 | 类库 |

### 3.4 项目文件配置

- 所有项目：`<LangVersion>latest</LangVersion>`，`<Nullable>enable</Nullable>`
- 类库优先使用 `netstandard2.0`（最大兼容性），如不需要 ASP.NET Core 特定功能
- 禁止使用 .NET 6.0 或其他已停止支持的版本

---

## 4. NuGet 包依赖管理

### 4.1 版本选择规则
- **使用最新稳定版本**：选择与目标框架兼容的最新稳定版 NuGet 包
- **避免预览版本**：除非关键功能必需，否则不使用预览版
- **确保积极维护**：优先选择 12 个月内有更新的包
- **检查包兼容性**：确保包与目标 .NET 版本兼容

### 4.2 避免重复依赖（重要）

**原则：如果引用的项目已经依赖了相同的包，就不要重复显式声明依赖。**

```xml
<!-- ✅ 正确：不需要再次引用 ProjectA 已依赖的包 -->
<ItemGroup>
  <ProjectReference Include="..\ProjectA\ProjectA.csproj" />
</ItemGroup>

<!-- ❌ 错误：重复引用了 ProjectA 已经依赖的包 -->
<ItemGroup>
  <ProjectReference Include="..\ProjectA\ProjectA.csproj" />
  <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
</ItemGroup>
```

**检查清单：**
- [ ] 检查所有引用的项目（`<ProjectReference>`）是否已经传递依赖了该包
- [ ] 使用 `dotnet list package` 命令查看实际引用的包
- [ ] 如果传递依赖的版本满足需求，就不要显式引用
- [ ] 只有在以下情况才显式引用：需要特定版本（覆盖传递依赖）、没有项目传递依赖该包、需要直接控制版本升级

### 4.3 版本格式

- ✅ 正确：`<PackageReference Include="PackageName" Version="8.0.4" />`
- ❌ 禁止浮动版本：`Version="8.0.*"`
- ❌ 禁止版本范围：`Version="[8.0,9.0)"`
- ❌ 禁止重复引用项目已传递依赖的包

### 4.4 版本对齐
- 解决方案中的所有项目**应该**对共享包使用**相同的主版本**
- 包版本**必须**与解决方案中的最低目标框架兼容
- 新增 NuGet 包必须有明确理由

---

## 5. 命名约定

### 5.1 通用命名

| 类型 | 规则 | 示例 |
|------|------|------|
| 接口 | `I` 前缀 + PascalCase | `IAgentEngine`, `IPipeline`, `IRouteTable` |
| 异步方法 | `Async` 后缀 | `ExecuteAsync`, `SendMessageAsync`, `IsAllowedAsync` |
| CancellationToken | 最后一个参数，默认 `default` | `Task DoAsync(..., CancellationToken ct = default)` |
| 公共抽象 | 定义在 Agent.Contracts | `IAgentPipeline`（接口）、`AgentConfig`（DTO） |
| 实现类 | PascalCase，在对应模块的 `src/` 下 | `Pipeline`, `EngineFactory` |
| 枚举 | PascalCase | `EngineFrameworkType` |
| 私有字段 | `_` 前缀 + camelCase | `_redis`, `_logger`, `_config` |
| 常量 | PascalCase，无特殊前缀 | — |
| 测试方法 | `方法名_场景_预期行为` | `SendMessage_WithValidInput_ReturnsResponse` |

### 5.2 类命名原则

- 类名**必须**简单且简短，避免冗长描述性前缀/后缀
- 去掉不必要的上下文重复词（命名空间已表达的信息）

```
✅ 正确：RegistryEntry, ConfigSnapshot, HeartbeatService
❌ 错误：EngineRegistryEntry, InMemoryConfigSnapshot, EngineHeartbeatService
```

### 5.3 可见性优先级

- 方法名**优先**使用 `private` 可见性
- 仅在确实需要跨类访问时才提升为 `internal`
- **禁止**使用 `public`，除非类型实现了必须公开的接口
- 可见性优先级：**`private` > `internal` > `public`**

### 5.4 模块特定命名

**Agent.Core：**

| 类别 | 约定 | 示例 |
|------|------|------|
| 中间件 | 语义化名称 | `AgentIdValidation`, `TenantValidation`, `Tracing`, `Auth`, `AuditLogging` |
| 引擎 | `{框架名}Engine` | `SemanticKernelEngine`, `MafEngine`, `OpenAIDriverEngine` |
| DI 扩展方法 | `Add{功能名}` | `AddAgentCore`, `AddSemanticKernelEngine`, `AddMafEngine` |

**Agent.Router：**

| 类别 | 约定 | 示例 |
|------|------|------|
| 项目名 | `OpenAgent.Core.{ServiceName}` | `OpenAgent.Core.Router` |
| 命名空间 | 与项目名一致 | `OpenAgent.Core.Router` |
| record 类型 | PascalCase | `RouteRequest`, `RouteResponse` |
| 配置节 | PascalCase + 冒号分隔层级 | `RouterSettings:RateLimiting:RequestsPerSecond` |

### 5.5 Redis Key 命名

| 类别 | 格式 | 示例 |
|------|------|------|
| 分布式锁 | `lock:{业务域}:{tenantId}:{entityId}` | `lock:conversation:{tenantId}:{conversationId}` |
| Agent 配置 | `agent:config:{agentId}` | `agent:config:default` |
| Engine 注册 | `engine:registry:{engineId}` | `engine:registry:engine-1` |

**分布式锁 key 命名规范：**
- 格式：`lock:conversation:{tenantId}:{conversationId}`
- 必须使用业务前缀（`lock:conversation:`）以便 SCAN 排查
- 禁止直接用裸 `conversationId` 作为 Redis key
- Owner token 格式：`Guid.NewGuid().ToString("N")`（32 位无连字符）
- 详见 `docs/modules/execution/conversation-lock.md`

---

## 6. 代码风格

### 6.1 命名空间与文件组织

- **必须**使用文件作用域命名空间（分号结尾），**禁止**使用块式大括号
- ✅ 正确：`namespace MyNamespace;`
- ❌ 错误：`namespace MyNamespace { ... }`
- 移除所有未使用的 using 语句
- 一个文件一个类，文件名与类名匹配

### 6.2 注释和字符串

- 所有注释**必须**使用**英文**
- 所有输出/显示的字符串字面量**必须**使用**英文**
- 代码注释和输出消息中**禁止**出现中文字符

```csharp
// ✅ Correct: Initialize the agent service
var agent = new AgentService();

// ❌ Incorrect: 初始化代理服务
var agent = new AgentService();
```

### 6.3 异步编程

- 所有 I/O 操作必须异步（`async/await`）
- 异步方法**必须**使用 `Async` 后缀命名
- 流式响应用 `IAsyncEnumerable<T>`
- 禁止 `async void`，只能用 `async Task`（事件处理程序除外）
- 禁止同步和异步混用在同一条调用链中
- 库代码中使用 `ConfigureAwait(false)` 避免上下文捕获
- 优先使用 `Task` 而不是 `ValueTask`（除非性能关键）

```csharp
// ✅ Correct
public async Task<User> GetUserAsync(int id)
{
    return await _repository.GetByIdAsync(id).ConfigureAwait(false);
}

// ❌ Incorrect
public async void GetUser(int id) { ... }
```

### 6.4 可空引用类型

- 所有项目**必须**启用可空引用类型：`<Nullable>enable</Nullable>`
- 正确处理可空警告，避免不必要的 `!` 操作符

### 6.5 其他规则

- 类型不明时禁止使用 `var`，使用显式类型声明
- 非关键操作（如冷归档写入）用 fire-and-forget

---

## 7. 项目结构

### 7.1 Agent.Core 项目结构

```
Agent.Core/
├── src/
│   ├── Core/               # 核心逻辑（Pipeline, Service, Middleware）
│   │   ├── Execution/      # 执行管线
│   │   ├── Capabilities/   # MCP / RAG / Skill
│   │   ├── Conversation/   # 会话存储与锁
│   │   └── Security/       # 中间件
│   └── Exten/              # 扩展方法
├── test/
│   └── OpenAgent.Core.Tests/
└── docs/                    # 已迁移到顶层 docs/
```

### 7.2 Agent.Engine 项目结构

```
Agent.Engine/
├── src/
│   ├── Engine/                    # 运行时类库
│   │   ├── Abstractions/          # IConfigSnapshot, IEngineRegistry
│   │   ├── Config/                # 配置读取
│   │   ├── Reload/                # 热更新
│   │   ├── Registry/              # 服务注册与心跳
│   │   └── Runtime/               # 运行时服务
│   └── Host/                      # Web API 宿主
│       ├── Extensions/            # EndpointExtensions
│       ├── Middleware/            # 异常处理中间件
│       └── Program.cs
├── test/
│   └── OpenAgent.Engine.Tests/
└── docs/                          # 已迁移到顶层 docs/
```

### 7.3 项目引用规则
- 使用 `<ProjectReference>` 而非包引用
- 避免循环依赖
- 依赖关系单向流动：Host → Engine → Abstractions
- **检查传递依赖**：避免重复引用项目已依赖的 NuGet 包

---

## 8. 模块特定编码模式

### 8.1 Agent.Core — 依赖注入

- 通过构造函数注入，不使用 Service Locator 模式
- 生命周期选择：
  - **Singleton**：无状态服务、注册表（`IToolRegistry`, `IRagRegistry`, `ILlmRegistry`, `IConversationStore`）
  - **Scoped**：请求级服务（`IAgentService`, `IAgentPipeline`, `IMcpClient`, `IRagService`, `IAgentMiddleware`）
- 扩展方法封装 DI 注册（`ServiceExtensions.AddAgentCore()`）
- 可重复调用的注册扩展必须幂等，优先使用 `TryAdd*`；Factory、Registry 等单例不得重复注册
- 中间件注册顺序必须保持：`AgentIdValidation → TenantValidation → Tracing → Auth → AuditLogging`
- Engine Host 的 `EngineRequestScopeMiddleware` 必须位于 `SseErrorHandlerMiddleware` 和
  `GlobalExceptionHandlerMiddleware` 之前，确保异常路径也携带请求 scope
- Redis 为可选依赖时，消费者通过 `GetService<IConnectionMultiplexer>()` 获取并处理 `null`，
  不得强制解析后再假设连接存在

```csharp
// 在 ServiceExtensions.cs 中注册
services.AddScoped<IAgentPipeline, Pipeline>();
services.AddSingleton<IAgentEngineFactory, EngineFactory>();
```

### 8.2 Agent.Core — 错误处理

- 业务异常使用 `AgentException` 及其子类
- 中间件异常由 Pipeline 统一捕获，转为 `AgentResponse(Success=false, ErrorCode=...)`
- 外部依赖异常不吞没，记录日志后向上传播或降级
- 使用 `AgentErrorCodes`（定义在 Agent.Contracts）进行错误分类
- 错误统一流经中间件管道，不要在业务代码中直接 try-catch 吞掉

### 8.3 Agent.Core — 日志

- 生产代码使用 `ILogger<T>` 注入，并通过 `[LoggerMessage]` 源生成方法记录；禁止直接调用
  `_logger.LogDebug/LogInformation/LogWarning/LogError`。
- 每个事件使用稳定、模块内唯一的 EventId；修改消息文本时不得复用为不同语义。
- 日志消息使用命名模板参数，不使用字符串插值，不记录 token、API Key、完整 prompt 或工具结果。
- 高频流式路径只记录请求开始、首块/完成摘要、取消和异常，不逐 chunk 记录。

```csharp
[LoggerMessage(EventId = 1350, Level = LogLevel.Information,
    Message = "Engine selected. Framework={Framework}")]
internal static partial void EngineSelected(ILogger logger, string framework);
```

| 模块 | EventId 区间 |
|------|--------------|
| Core / ToolCall | `1000-2199` |
| Router | `3000-3199` |
| Hosting | `3000-3005`（独立日志类别） |
| Engine | `4000-4199` |
| Channels | `5000-5199` |
| AuditStation | `6000-6099` |
| RedisTool | `7000-7099` |

### 8.4 Agent.Core — Engine 模式

- 所有 LLM 引擎实现 `IAgentEngine`（ChatCompletion + StreamChatCompletion）
- 新引擎放在 `src/<EngineName>/` 下，带独立的 `.csproj`
- 通过 `IAgentEngineFactory` 注册

### 8.5 Agent.Router — 日志规范

Router 同样只能调用 `RouterLog` 中的源生成日志。Debug 用于路由细节，Information 用于关键
业务事件，Warning 用于可降级异常，Error 用于不可恢复或转发失败。

#### 结构化日志

- 使用命名占位符 `{PropertyName}`，不要用字符串插值
- 审计日志统一前缀 `[Audit]`
- 关键业务日志必须包含 TraceId

#### 审计日志格式

```
[Audit] {TraceId} | {Method} {Path} | User: {UserId} | Tenant: {TenantId} | Query: {Query} | Status: {StatusCode} | Outcome: {Outcome} | Duration: {DurationMs}ms
```

### 8.6 Agent.Router — 错误处理模式

#### 外部依赖降级（Fail-open）

当 Redis 不可用时，所有功能降级而非报错：

```csharp
catch (RedisConnectionException ex)
{
    RouterLog.RedisUnavailable(logger, ex);
    return true; // 或 null，取决于降级策略
}
```

| 组件 | Redis 不可用时的降级行为 |
|------|--------------------------|
| RedisRateLimiter | 放行（返回 true） |
| RedisServiceDiscoveryRouteTable | 返回 null（触发静态配置回退） |
| AgentVisibilityService | 无 ACL 条目时默认允许 |
| IDistributedCache (幂等) | 旁路绕过，请求正常转发 |

#### HTTP 错误响应

| 场景 | 状态码 | 响应体 |
|------|--------|--------|
| 未认证 | 401 | — |
| 租户不匹配 | 403 | — |
| Agent 可见性校验失败 | 403 | — |
| 超出限流 | 429 | — |
| 无法路由 | 400 | `{ Error: "Unable to determine target service" }` |
| 下游超时 | 504 | `{ Status, Message, Fallback, TraceId }` |
| 下游不可用 | 503 | `{ Status, Message, Fallback, TraceId }` |

### 8.7 Agent.Router — DI 注册模式

- 所有服务接口注册为 Singleton（`AddSingleton`），因为 Router 是无状态网关
- `IAgentUserContext` 注册为 Scoped（`AddScoped`），因为依赖 HttpContext
- 配置通过 `IConfiguration` 注入，不使用 Options 模式
- Redis 连接串通过 `IConfiguration.GetConnectionString("Redis")` 读取

### 8.8 Agent.Router — 异步模式

- 所有异步方法接受 `CancellationToken` 参数（默认值 `default`）
- `SimpleIntentRecognizer.RecognizeAsync` 当前未检查 CancellationToken，返回同步结果
- YARP 转发使用 `IHttpForwarder.SendAsync`

### 8.9 Agent.Channels — 边界与运行约束

- Channels 只能通过 `IChannelRouterClient` 调 Router；修复 Channels 时不得把业务逻辑下沉到
  Router，也不得引用 Core/Engine。
- 中间件顺序：异常处理 `50`、身份上下文 `100`、关联 ID `200`、限流 `300`、审计 `350`、
  分发 `400`；`ChannelRequestScopeMiddleware` 保持 ASP.NET Core 最外层。
- `TraceId` 解析优先级为 `X-Trace-Id` → `Activity.Current.Id` → `HttpContext.TraceIdentifier`，
  并原样传给 Router。Core scope 中 `TenantId` 缺失时保持 `null`。
- 外部 HTTP 一律使用 `IHttpClientFactory`；`RouterClientOptions.InternalServiceName` 必须随内部
  token 一起传播。
- Teams Playground 使用 `DefaultTenantId=tenant-001`；生产环境从真实 channel data 解析。
  Outlook 默认关闭，只有显式配置时启用。
- Cron Job 标记 `[DisallowConcurrentExecution]`，单次异常记录后结束本次触发，不得终止调度器。
- 系统触发的 MessageId 格式为 `{JobName}-{Guid:N}`，确保去重中间件不会吞掉后续触发。
- 与时间相关的测试注入 `TimeProvider` 或可控延迟，禁止用短时 `Thread.Sleep` 猜测时序。

---

## 9. 测试规范

### 9.1 测试框架与位置

| 测试类型 | 框架 | 位置 |
|---------|------|------|
| Core 单元测试 | xUnit + Moq | `Agent.Core/<Module>.Tests/` |
| Engine 单元测试 | xUnit + Moq | `Agent.Engine/test/` |
| Router 单元测试 | xUnit 2.6.2 + Moq 4.20.70 | `Agent.Router/test/` |
| 集成测试 | MSTest | `TestCode/Agent.TestEngine/` |
| E2E 测试 | PowerShell | `TestCode/scripts/` |

### 9.2 测试代码规范

- 测试类名与被测类名对应，后缀 `Tests`
- 测试方法命名：`MethodName_Scenario_ExpectedResult`
- 使用 Arrange-Act-Assert 模式
- 每个测试只验证一个行为
- 用 `[Fact]` 标记简单用例，`[Theory]` + `[InlineData]` 标记参数化用例
- **参数化优先**：仅输入/输出不同的同类用例，必须用 `[Theory]` + `[InlineData]` 或 `[MemberData]` 合并，禁止复制粘贴多个 `[Fact]`
- **禁止为测试改可见性**：不允许将 `private` 方法改为 `internal`/`public` 以便测试，应通过公共 API 间接验证私有方法行为
- Mock 通过构造函数注入
- 测试替身集中在 `TestDoubles/` 目录
- 配置使用 `ConfigurationBuilder.AddInMemoryCollection`
- 通过 `InternalsVisibleTo` 访问内部类型进行测试
- 集成测试用 `[ClassInitialize]`/`[ClassCleanup]` 管理宿主生命周期
- HTTP 依赖使用自定义 `DelegatingHandler` mock，不引入额外依赖

```csharp
[Test]
public async Task GetUserAsync_InvalidId_ThrowsNotFoundException()
{
    // Arrange
    var invalidId = -1;

    // Act & Assert
    Assert.ThrowsAsync<NotFoundException>(() => _service.GetUserAsync(invalidId));
}
```

### 9.3 Agent.Engine 测试结构

| 测试目录 | 被测组件 |
|----------|----------|
| `Config/` | `ConfigProvider`, `ConfigSnapshot`, `HotReloadService` |
| `HealthChecks/` | `ConfigHealthCheck`, `LlmHealthCheck`, `RedisHealthCheck` |
| `Hosting/` | 端到端集成测试 |
| `TestDoubles/` | `FakeRedisConnectionProvider` |

### 9.4 覆盖率目标

- 核心模块 ≥ 80%
- 关键业务逻辑**必须**有单元测试覆盖
- 变更提交前至少应考虑：
  - 是否需要编译验证
  - 是否需要补充有价值的测试
  - 是否影响已有公共契约
  - 是否需要同步更新文档

### 9.5 自主验证

Agent 修改代码后必须按照 `.agent/skills/verify-changes.md` 自主决定验证策略，见该文档中的决策表。

---

## 10. 构建和编译要求

### 10.1 编译要求
- 所有解决方案**必须**无错误编译
- 所有单元测试**必须**通过
- **禁止**出现未使用引用的警告

### 10.2 全局配置

在解决方案根目录创建 `Directory.Build.props` 统一配置：

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

---

## 11. 合规性检查清单

PR 合并前**必须**验证：

### 11.1 .NET 版本合规性
- [ ] 所有项目 targeting .NET 8.0 或 .NET Standard 2.0/2.1
- [ ] **没有使用 .NET 6.0 或其他已过期版本**
- [ ] 目标框架是最低可行版本
- [ ] 类库在可能时使用 .NET Standard

### 11.2 包依赖合规性
- [ ] 所有 NuGet 包与目标框架兼容
- [ ] 包版本是明确的（无浮动版本）
- [ ] **没有重复引用项目已依赖的包**
- [ ] 包是积极维护的（12 个月内有更新）
- [ ] 使用 `dotnet list package` 验证实际依赖

### 11.3 代码规范合规性
- [ ] 使用文件作用域命名空间
- [ ] 注释和字符串使用英文
- [ ] 无未使用的 using 语句
- [ ] 启用了可空引用类型
- [ ] 异步方法命名正确（Async 后缀）
- [ ] 类名简短无冗余前缀
- [ ] 文件名与类名匹配且简短
- [ ] 可见性最小化（private 优先，internal 次之，public 仅必要时）
- [ ] 每个类职责单一
- [ ] 未违反项目分层规则

### 11.4 测试合规性
- [ ] 所有单元测试通过
- [ ] 关键逻辑有测试覆盖
- [ ] 测试项目名称规范

### 11.5 编译合规性
- [ ] 解决方案无错误编译
- [ ] 无警告（特别是未使用引用警告）

---

## 12. 例外流程

任何偏离本规范的情况**必须**遵循以下流程：

1. **书面说明**：文档化为什么不能使用标准做法
2. **影响分析**：评估对其他项目和部署的影响
3. **审批**：必须经过架构团队审查和批准
4. **记录在案**：在代码中添加注释说明例外原因

---

## 13. 迁移路径

对于需要合规的现有项目：

1. **审计依赖**：列出所有依赖及其框架要求
2. **识别过期版本**：特别检查是否使用了 .NET 6.0 或其他已过期版本
3. **检查重复依赖**：使用 `dotnet list package` 识别重复引用的包
4. **渐进式更新**：逐步更新项目文件
5. **完整测试**：每次更改后运行完整测试套件
6. **更新文档**：同步更新相关文档

---

## 14. 禁止事项

- ❌ 违反依赖方向添加项目引用
- ❌ 修改 Agent.Contracts 公共接口前不检查所有消费者
- ❌ 无理由添加 NuGet 包
- ❌ 类型不明时使用 `var`
- ❌ 混合 sync/async 在同一调用链
- ❌ 使用 `async void`
- ❌ 直接修改 Redis 中的 Agent 配置（应通过 Engine API 或 RedisTool 管理）
- ❌ 使用浮动版本或版本范围的 PackageReference
- ❌ 重复引用项目已传递依赖的 NuGet 包
- ❌ 使用 .NET 6.0 或其他已过期版本
- ❌ 注释和字符串使用中文

---

## 附录 A：常用项目配置模板

### A.1 Web API 项目
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

### A.2 类库（推荐 .NET Standard）
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

### A.3 测试项目
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

### A.4 多目标框架（仅在必要时使用）
```xml
<PropertyGroup>
  <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
</PropertyGroup>
```

---

## 附录 B：依赖检查命令

```bash
# 查看项目引用的所有包（包括传递依赖）
dotnet list package

# 查看特定项目的包依赖
dotnet list <project.csproj> package

# 查看包依赖树
dotnet list package --include-transitive

# 检查包过时情况
dotnet list package --outdated
```
