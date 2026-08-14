# 开发指南

OpenAgent 项目的代码审查、功能规划、测试编写和集成排查指南。

---

## 1. 代码审查

### 分层审查清单

**Agent.Contracts（接口层）**
- [ ] 公共接口签名向后兼容
- [ ] 新增接口属于 Contracts 层（无实现依赖）
- [ ] DTO JSON 序列化使用 camelCase
- [ ] 错误码使用 `AgentErrorCode` 统一定义
- [ ] 未引入对其他项目的引用（Contracts 是叶子节点）

**Agent.Core（核心逻辑层）**
- [ ] Engine Host 中间件顺序：AgentUserContextMiddleware → EngineAdmissionMiddleware → AgentExceptionHandlerMiddleware
- [ ] 异步方法正确传播 CancellationToken
- [ ] 流式 `IAsyncEnumerable<T>` 正确释放资源
- [ ] DI 注册：中间件 `AddScoped`，注册表 `AddSingleton`
- [ ] 错误通过中间件管道传播（不吞掉异常）

**Agent.Engine / Host（宿主层）**
- [ ] 端点遵循 `/api/v1/agent/` 前缀
- [ ] appsettings.json 连接配置正确
- [ ] Dockerfile（如需要）

**测试层**
- [ ] Core 逻辑 → xunit 单元测试
- [ ] 跨服务行为 → xUnit + Testcontainers 集成测试
- [ ] 端到端流程 → PowerShell E2E 脚本
- [ ] 测试命名：`方法名_场景_预期行为`

### 通用检查

- [ ] 未违反依赖方向（Contracts ← Core ← Engine/Router ← Host）
- [ ] 无 `async void`
- [ ] 无 sync/async 混用
- [ ] `var` 只在类型明显时使用
- [ ] Nullable 引用类型正确处理
- [ ] 新增 NuGet 包有明确理由

### 安全检查

- [ ] 认证/授权通过中间件统一处理
- [ ] API Key / Token 未硬编码
- [ ] SQL 查询参数化
- [ ] 敏感数据未记录到日志

---

## 2. 功能规划

### 确定归属层

| 需求 | 归属 |
|------|------|
| 新接口/DTO/错误码 | Contracts |
| 新 Engine / Pipeline 中间件 | Core |
| 新 HTTP 端点或后台服务 | Engine |
| 新路由/网关策略 | Router |
| 新 DI 或认证基础设施 | Hosting |
| 测试辅助 | Backend/tests/OpenAgent.{Project}.Tests |

> 跨层功能从最内层（Contracts）开始定义，向外逐层实现。

### 架构影响检查

- [ ] 是否违反依赖方向？
- [ ] 是否需要新增 LLM Provider 适配？
- [ ] 是否需要新 Pipeline 中间件？
- [ ] 是否需要新集成点？

### 文档更新矩阵

| 需更新的文档 | 条件 |
|-------------|------|
| `docs/modules/` | 新增核心能力 |
| `docs/integrations/` | 新增外部集成 |
| `docs/overview/` | 架构变化 |
| `.agent/rules/coding-conventions.md` | 编码规范变化 |
| `.agent/skills/` | 新增工作流 |

---

## 3. 测试编写

### 测试模板（xunit）

```csharp
[Fact]
public async Task MethodName_Scenario_ExpectedBehavior()
{
    // Arrange
    var mockEngine = new MockEngine();
    var pipeline = new Pipeline(mockEngine);

    // Act
    var result = await pipeline.ExecuteAsync(request);

    // Assert
    Assert.NotNull(result);
    Assert.Equal(expected, result.Status);
}

[Theory]
[InlineData("input1", "expected1")]
[InlineData("input2", "expected2")]
public async Task MethodName_WithDifferentInputs_ReturnsExpected(
    string input, string expected)
{
    // 同上 Arrange/Act/Assert 模式
}
```

### 关键规则

- 测试命名：`方法名_场景_预期行为`
- Mock 通过 `InternalsVisibleTo` 访问内部类型
- 优先复用已有 `MockEngine`
- 每个公共接口方法至少一个测试
- 必须覆盖错误路径（不仅仅是 happy path）
- 多个相似用例必须用 `[Theory]` 参数化，禁止复制粘贴 `[Fact]`

### 集成测试

- 使用 xUnit + Testcontainers（PostgreSQL/Redis）
- 位置：`Backend/tests/OpenAgent.Infrastructure.Tests/`、`Backend/tests/OpenAgent.Engine.Tests/`
- 无需外部服务或 API Key

---

## 4. 集成问题排查

由外到内逐层检查：

### 服务连通性

```powershell
docker compose ps
```

预期端口：8080（chat）、5001（router）、5208（engine）、5432（postgres）、6379（redis）、9000/9001（minio）

### LLM 提供商问题

1. 检查 LLM Provider：在工作台设置中创建 LLM Provider（参考根 `README.md`）
2. 检查提供商配置：RedisTool 查看 `llm:registry:<provider>`
3. 用 curl 直接测试 API 可达性

### MCP 问题

1. MCP 服务健康：`curl http://localhost:5208/health`
2. 工具发现：检查 MCP 配置是否注册到 Engine
3. 配置检查：MCP 由 Engine 内嵌，检查 `appsettings.json` 的 Mcp 配置节

### 完整排查手册

详见 `docs/trace-troubleshoot.md`（含 health、metrics 与 trace 排查步骤）。
