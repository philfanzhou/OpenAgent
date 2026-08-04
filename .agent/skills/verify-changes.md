# 自主功能验证

## 用途
Agent 修改代码后，**自主决定**需要运行哪些验证步骤，无需用户逐一指示。本技能定义了从"改了什么"到"验证什么"的完整决策链路。

## 触发条件
- **每次代码修改完成后自动触发**（Agent 应在编辑后主动运行验证）
- 用户要求"验证改动"、"检查代码"、"确认没问题"
- 合并前 / 提交前

## 核心原则

1. **渐进式验证**：从最快、最便宜的检查开始，失败则不继续
2. **范围感知**：改动越大，验证越深；只改一行注释则不跑 E2E
3. **失败即停**：编译失败 → 不跑测试；单测失败 → 先修再继续
4. **自主决策**：Agent 根据改动范围自行决定验证深度，不确定时偏保守（多验证）

---

## 第一步：检测改动范围

Agent 在修改代码后，执行以下命令获取变更文件列表：

```bash
git diff --name-only
```

对于**未跟踪的新文件**：
```bash
git ls-files --others --exclude-standard
```

然后按路径分类：

| 路径匹配 | 模块 | 测试项目 |
|---------|------|---------|
| `Backend/**/Agent.Contracts/**` | Contracts（公共契约） | 所有消费者 |
| `Backend/**/Agent.Core/src/Core/**` | Core 核心逻辑 | `Agent.Core/test/...Tests/` |
| `Backend/**/Agent.Core/src/OpenAIDriver/**` | OpenAI Driver | `Agent.Core/test/...Tests/OpenAIDriver/` |
| `Backend/**/Agent.Core/src/MAF/**` | MAF 引擎 | `Agent.Core/test/...Tests/` |
| `Backend/**/Agent.Core/src/SemanticKernel/**` | SK 引擎 | `Agent.Core/test/...Tests/` |
| `Backend/**/Agent.Core/src/Mock/**` | Mock 引擎 | 通常不需要额外测试 |
| `Backend/**/Agent.Engine/src/Engine/**` | Engine 运行时 | `Agent.Engine/test/...Tests/` |
| `Backend/**/Agent.Engine/src/Host/**` | Engine 宿主 | `Agent.Engine/test/...Tests/Hosting/` |
| `Backend/**/Agent.Router/src/**` | Router 网关 | `Agent.Router/test/...Tests/` |
| `Backend/**/Agent.Hosting/src/**` | Hosting 基础设施 | 影响所有宿主项目 |
| `TestCode/**` | 测试代码 | 运行受影响的测试项目 |
| `.agent/**`、`*.md` | 文档/配置 | 无需编译验证 |
| `*.csproj`、`*.sln` | 项目配置 | 完整重建 |

---

## 第二步：选择验证级别

根据改动类型和范围，Agent 自主选择验证级别：

### 级别 0 — 编译检查（~10秒）
**何时用**：改了 `.cs` 文件但改动很小（<20行）、只改了注释/字符串、改了 XML 文档注释

**执行**：
```powershell
dotnet build <affected-sln> --no-restore
```

### 级别 1 — 单元测试（~30秒-2分钟）
**何时用**：改了逻辑代码、新增方法/类、修了 bug、改了 `internal` 可见性

**执行**：
```powershell
dotnet build <affected-sln> && dotnet test <affected-sln> --no-build
```

### 级别 2 — 编码规范检查（~10秒）
**何时用**：总是执行（除非只改了文档）

**检查项**：按 `.agent/rules/coding-conventions.md` §11 合规检查清单逐项验证。

### 级别 3 — 集成测试（~1-3 分钟）
**何时用**：改了跨服务接口、改了公共 Contract、改了序列化/反序列化逻辑、改了 Engine 消息处理流程（包括流式传输）、改了 Router 路由逻辑

**前置条件**：无需外部服务

**执行**：
```powershell
dotnet test TestCode/TestEnv.sln --no-build
```

集成测试使用 MSTest + WireMock + FakeRedis，Engine 以 ASP.NET Core 宿主运行。
覆盖：Engine 端点（chat、streaming、SSE、NDJSON）、MCP 协议、Skill 调用、Agent 配置热加载、SSO 认证等。
详见 `TestCode/Agent.TestEngine/` 和 `TestCode/Agent.TestFramework/README.md`。

### 级别 3b — 真 E2E 测试（~5-10 分钟）
**何时用**：Level 3 通过后需要验证真实 LLM 调用链、真实 SSO/Redis 基础设施、发布前验证

**前置条件**：`.env` 已配置 API Key，5 个测试服务已启动

**执行**：
```powershell
cd TestCode/scripts
./test-e2e.ps1 -SkipBuild -SkipLlmTests    # 不含真实 LLM
./test-e2e.ps1 -SkipBuild -Provider <id>   # 含真实 LLM
```

> **注意**：真 E2E 包含两类测试：
> 1. PowerShell 集成脚本（`run-all-tests.ps1`）— 通过 HTTP 调用运行中的服务，mock LLM
> 2. MSTest `RealServiceTests`（9 个测试）— 连接真实 SSO + Redis + LLM，标记为 "Not for CI"

### 级别 4 — 完整 E2E（~5-10分钟）
**何时用**：改了跨多个服务的关键流程、发布前验证、改动涉及 LLM 调用链、改了 Agent 配置 JSON 结构

**前置条件**：`.env` 已配置 API Key

**执行**：
```powershell
cd TestCode/scripts
./test-e2e.ps1 -Provider <available-provider>
```

---

## 第三步：决策表（Agent 自主判断）

| 改动场景 | 最小验证 | 推荐验证 | 备注 |
|---------|---------|---------|------|
| 只改注释/文档/`.md` | 无需验证 | 无需验证 | 如果改了 AGENTS.md 需要用户确认 |
| 新加一个 `private` 方法 | Level 0 | Level 1 | 至少确认编译通过 |
| 修改 `internal` 方法逻辑 | Level 0 + 1 | Level 2 | 需要跑对应模块单测 |
| 修改 `public` 接口签名 | Level 0 + 1 | Level 3 | 所有消费者受影响 |
| 修改 `Agent.Contracts` 类型 | Level 1（全部） | Level 3 + 3b | **必须跑全部单测 + 集成测试** |
| 新增 Engine 实现 | Level 0 + 1 | Level 3 | 新 Engine 需要集成验证 |
| 修改 Router 路由逻辑 | Level 0 + 1 | Level 3 | 影响请求分发 |
| 修改流式传输 / SSE / 消息管道 | Level 0 + 1 | Level 3 | Engine 消息处理流程变更 |
| 修改序列化/反序列化 | Level 0 + 1 | Level 3 + 3b | 可能影响服务间通信，需真 E2E 验证 |
| 新增/修改 `.csproj` NuGet | Level 0 | Level 1 | 确认包兼容性和传递依赖 |
| 修改中间件 | Level 0 + 1 | Level 3 | 中间件顺序很重要 |
| 修改 DI 注册 | Level 0 | Level 1 | 确认无循环依赖 |
| 修改 `appsettings.json` | Level 0 | Level 3 | 配置变更需集成验证 |
| 修改 TestCode 服务 | Level 0 | Level 3 | 测试基础设施变更 |
| 发布前最终检查 | Level 1（全部） | Level 3 + 3b | 全部 solution + 集成 + E2E |

---

## 第四步：执行验证

### 4.1 确定受影响的 Solution

参见 `.agent/skills/build-and-test.md` 步骤 1 中的模块→Solution 映射表。

### 4.2 执行顺序（强制）

```
Level 0 → Level 1 → Level 2 → Level 3 → Level 3b (可选)
   ↓         ↓         ↓         ↓            ↓
 失败即停   失败即停   继续      继续          继续
```

- Level 0 失败 → **修复编译错误，重新开始**
- Level 1 失败 → **分析失败原因，报告用户**
- Level 2 失败 → 记录不合规项，Agent 自行修复
- Level 3 失败 → 分析失败原因，报告用户
- Level 3b 失败 → 提取失败详情，报告用户（真 E2E 受网络/服务可用性影响较大，允许标记为环境问题）

### 4.3 跳过条件

Agent 可以跳过某个级别的条件：
- **Level 0 可跳过**：只改了非 `.cs` 文件
- **Level 1 可跳过**：改动在 Mock 项目或测试项目本身
- **Level 3/3b 可跳过**：改动仅影响不参与集成测试的代码路径，且用户明确表示不需要
- **任何级别可跳过**：用户明确说"不用跑测试"、"skip tests"

---

## 第五步：结果解释

### 编译失败
```
❌ Level 0 FAILED: dotnet build
   Error: <file>(<line>): error CS####: <message>
   Action: 修复编译错误后重新验证
```

### 测试失败
```
❌ Level 1 FAILED: dotnet test
   Failed: X/Y tests — <failed-test-names>
   Action: 分析失败原因（回归？环境？测试本身？），报告用户
```

### 全部通过
```
✅ Level 0 PASSED: dotnet build — 0 errors, 0 warnings
✅ Level 1 PASSED: dotnet test — N/N passed
✅ Level 2 PASSED: Convention check — 0 violations
```

---

## 第六步：特殊场景

### 场景 A：改动了 Agent.Contracts
```
⚠️ PUBLIC CONTRACT CHANGE DETECTED
   受影响消费者需要重新编译和测试。
   自动升级到 Level 1（全部 solution） + Level 3。
```

### 场景 B：新增项目引用
```
⚠️ PROJECT REFERENCE ADDED
   检查依赖方向是否违反分层规则（Contracts ← Core ← Engine/Router ← Host）
   如违反：拒绝并提示用户
```

### 场景 C：改动涉及多模块
```
⚠️ CROSS-MODULE CHANGE DETECTED
   Module: Core → 影响 Engine, Router
   自动升级到 Level 3（集成测试）
```

### 场景 D：只改了测试代码
```
ℹ️ TEST-ONLY CHANGE
   运行受影响的测试项目确认改动正确。
   不需要 E2E。
```

---

## 自动化建议（可选）

如需 Agent 在**每次修改后自动触发验证**，可以在 `.claude/settings.json` 中配置 Hook：

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "dotnet build Backend/OpenAgent/Agent.Core/OpenAgent.Core.sln --no-restore 2>&1 | tail -5"
          }
        ]
      }
    ]
  }
}
```

> **注意**：Hook 配置需要用户确认后才会生效。Agent 不应自行添加 Hook，应提示用户。

---

## 与其他技能的关系

具体 solution、项目路径和命令以 `.agent/skills/build-and-test.md` 为准；本技能只负责根据改动
范围选择验证级别，不复制或维护第二套命令清单。

| 技能 | 关系 |
|------|------|
| `build-and-test` | 本技能调用它执行 Level 0 + 1 + 3 |
| `e2e-test` | 本技能调用它执行 Level 3b（真 E2E，需真实外部服务） |
| `code-review` | 编码规范检查（Level 2）与 code review 互补 |
| `service-lifecycle` | Level 3b 的前置步骤（启动/停止测试服务） |

---

## 验证方法
本技能本身通过以下方式验证：
- 按照决策表实际执行一次验证流程
- 确认各级别的判断逻辑正确触发
- 确认失败即停逻辑生效
