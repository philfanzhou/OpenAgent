# 构建与测试

## 用途
构建指定模块的 solution 并运行测试（单元测试或集成测试）。

## 触发条件
- 用户要求"构建"、"编译"、"运行测试"、"验证代码"
- 修改代码后需要确认编译通过和测试不挂

## 输入参数
- `module`: 要测试的模块，可选值：
  - `core` — OpenAgent.Core（类库 + xunit 单元测试）
  - `engine` — OpenAgent.Engine（引擎 + xunit 单元测试）
  - `router` — OpenAgent.Router（网关 + xunit 单元测试）
  - `hosting` — OpenAgent.Hosting（共享宿主 + xunit 单元测试）
  - `all`（默认）— 全部模块

## 工作流程

### 步骤 1: 确定要构建的 solution

| 参数值 | Solution 路径 | 说明 |
|--------|--------------|------|
| `core` | `Backend/OpenAgent.sln` | xunit |
| `engine` | `Backend/OpenAgent.sln` | xunit |
| `router` | `Backend/OpenAgent.sln` | xunit |
| `hosting` | `Backend/OpenAgent.sln` | xunit |
| `all` | `Backend/OpenAgent.sln` | 全部模块 |

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

#### 3b. 环境门控测试

默认测试套件不依赖任何外部服务。两批测试默认跳过，仅由环境变量开启：

- `OpenAgent.Runner.Tests` 的 `[BubblewrapFact]`（Bubblewrap 沙箱执行）需 Linux + `RUN_CODEACT_BWRAP_TESTS=1`，CI 的 codeact job 在 Linux 上运行；
- `OpenAgent.Core.Tests` 的 `[RunnerIntegrationFact]`（连接真实 Runner）需 `RUN_CODEACT_RUNNER_TESTS=1` + `CODEACT_TEST_RUNNER_ENDPOINT/KEY`。

容器级/发布产物级验证由 `scripts/` 下的冒烟脚本承担（`test-codeact-runner.py`、`test-bubblewrap-basic.sh`），不属于 `dotnet test` 范围。

### 步骤 4: 如有测试失败
- 提取失败测试名称和错误信息
- 不要自动修复，先报告给用户

## 注意
- 全部测试项目使用 **xunit + Moq**；默认 `dotnet test Backend/OpenAgent.sln` 无需 Docker、PostgreSQL、Redis 等外部服务
- 依赖真实容器的集成测试已移除（见 `Backend/tests/README.md`）；沙箱/真 Runner 测试由环境变量门控（见 3b）
- **真 E2E 测试**（连接真实 Redis + LLM）需要单独配置，见 `e2e-test` 技能
- Agent 根据改动范围自主决定调用哪些模块（改 `.cs` → 单测；改 Contracts → 全量）
- 所有路径相对于仓库根目录 `<repository-root>`

## 参考文件
- 测试规范：`.agent/rules/coding-conventions.md`（第 5 节）
- 测试分层说明：`Backend/tests/README.md`
- 项目架构：`AGENTS.md`

## 验证方法
- 所有 solution 构建成功（exit code 0）
- 所有单元测试通过，无失败
- Backend/OpenAgent.sln 全部测试通过，无失败
