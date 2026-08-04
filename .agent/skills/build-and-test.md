# 构建与测试

## 用途
构建指定模块的 solution 并运行测试（单元测试或集成测试）。

## 触发条件
- 用户要求"构建"、"编译"、"运行测试"、"验证代码"
- 修改代码后需要确认编译通过和测试不挂

## 输入参数
- `module`: 要测试的模块，可选值：
  - `core` — Agent.Core（类库 + xunit 单元测试）
  - `engine` — Agent.Engine（引擎 + xunit 单元测试）
  - `router` — Agent.Router（网关 + xunit 单元测试）
  - `channels` — Agent.Channels（通道 + xunit 单元测试）
  - `hosting` — Agent.Hosting（共享宿主 + xunit 单元测试）
  - `audit` — Agent.AuditStation（服务 + xunit 单元测试）
  - `integration` — 集成测试（MSTest + WireMock，无需外部服务）
  - `all`（默认）— 全部模块

## 工作流程

### 步骤 1: 确定要构建的 solution

| 参数值 | Solution 路径 | 说明 |
|--------|--------------|------|
| `core` | `Backend/OpenAgent.sln` | xunit |
| `engine` | `Backend/OpenAgent.sln` | xunit |
| `router` | `Backend/OpenAgent.sln` | xunit |
| `channels` | `Backend/OpenAgent.sln` | xunit |
| `hosting` | `Backend/OpenAgent.sln` | xunit |
| `audit` | `Backend/OpenAgent.sln` | xunit；构建时可传 `-p:SkipFrontendBuild=true` |
| `integration` | `TestCode/TestEnv.sln` | MSTest + WireMock + FakeRedis，**无需外部服务** |
| `all` | 以上全部 | 先跑 Backend/，再跑 TestCode/ |

### 步骤 2: 构建
对每个 solution 执行：
```powershell
dotnet build <sln-path>
```
如果构建失败，报告错误并停止后续步骤。

### 步骤 3: 运行测试

#### 3a. 单元测试

```powershell
dotnet test <sln-path> --no-build
```

报告每个项目的测试结果（通过/失败/跳过数）。

#### 3b. 集成测试（TestCode/TestEnv.sln）

集成测试使用 MSTest + WireMock + FakeRedis，Engine 以 ASP.NET Core 宿主运行并通过 HTTP 调用。
**不需要任何外部服务或 API Key**，可直接运行：

```powershell
dotnet test TestCode/TestEnv.sln --no-build
```

集成测试覆盖：Engine 端点（chat、streaming、SSE、NDJSON）、MCP 协议、Skill 调用、
Agent 配置热加载、SSO 认证等。详见 `TestCode/Agent.TestEngine/`。

### 步骤 4: 如有测试失败
- 提取失败测试名称和错误信息
- 不要自动修复，先报告给用户

## 注意
- Core / Engine / Router 使用 **xunit**，集成测试（TestCode/TestEnv.sln）使用 **MSTest**
- 集成测试使用 WireMock 模拟 LLM/SSO/MCP/Skill，FakeRedis 模拟 Redis — **无需外部服务或 API Key**
- **真 E2E 测试**（连接真实 SSO + Redis + LLM）需要单独配置，见 `e2e-test` 技能
- Agent 根据改动范围自主决定调用哪些模块（改 `.cs` → 单测；改 Contracts → 全量）
- 所有路径相对于仓库根目录 `<repository-root>`

## 参考文件
- 测试规范：`.agent/rules/coding-conventions.md`（第 5 节）
- 项目架构：`AGENTS.md`
- 集成测试详情：`TestCode/Agent.TestFramework/README.md` — 测试框架架构、WireMock 用法、场景驱动测试

## 验证方法
- 所有 solution 构建成功（exit code 0）
- 所有单元测试通过，无失败
- 集成测试（TestCode/TestEnv.sln）通过，无失败
