# PR-10：前端类型代码生成

| 项 | 内容 |
|----|------|
| 状态 | 规划（2026-09 会话架构审查后续浪潮） |
| 基线 | main@9b694ab |
| 类型 | 工程化基建（脚本 + CI 门禁），后端仅加一个工具项目 |
| 依赖 | 与 PR-8（Metadata 类型化）协同收益最大，但无硬依赖 |

## 1. 动机

### 1.1 手写镜像的规模与漂移实例

`Frontend/OpenAgent.Chat/src/types.ts` 共 **358 行**、约 40 个 interface/type，逐字段手工
镜像 C# Contracts：`ConversationMessage`（:25-43 ↔ `ConversationMessage.cs:5-21`）、
`ConversationRecord`（:90-103）、`LlmInteractionRecord`（:133-157）、`ContextSummary`
（:105-122）、`McpConfig`/`SkillsConfig`/`RagConfig`/`LlmProviderProfile`
（:159-241 ↔ `AgentConfig.cs` 各节）、`AgentConfigEntity.config`（:273-287）等。
**17 个 ts/vue 文件** import 该文件（api.ts、App.vue、各 presentation 模块与测试）。

漂移是现实而非假设：

- 后端 `ConversationMessage.TraceId`（`ConversationMessage.cs:16`，d449647 引入）在
  types.ts 的 `ConversationMessage` 里**至今缺失**——前端只能从 `StreamEvent.traceId`
  （:294）和 `LlmInteractionRecord.traceId` 侧面拿到轮次键。
- `d449647`（后端 41 文件，加 llm-interactions API）与 `9ede3e1`
  （前端 "feat(chat): debug dialog for session llm interaction records"）是**同一逻辑变更的
  双侧手改**：先手写 C# 契约，再手写 TS 镜像，没有任何门禁拦截拼错/漏写。

### 1.2 工程现状（核对结论）

- 前端：Vue 3 + Vite + vitest + `vue-tsc --noEmit`（`package.json:6-12`），
  pnpm 11.21.0 / Node 22（`.github/workflows/ci.yml` frontend job）；无任何 codegen 依赖。
- 后端：**Swashbuckle.AspNetCore 6.6.2 已在**（`Backend/Directory.Packages.props:27`），
  `AddSwaggerGen()` + dev 环境 `UseSwagger()/UseSwaggerUI()` 已接线
  （`OpenAgent.Hosting/ServiceCollectionExtensions.cs:67-70`、`BuilderExtensions.cs:33-37`、
  开关 `AgentHostOptions.cs:6`）。但端点是 minimal API + 抽取的私有静态 handler +
  `Results.Ok(...)`（如 `ConversationEndpointExtensions.cs:37-50`），ASP.NET Core 对
  `Results`（非 `TypedResults`）与外提方法**不推断响应类型元数据**，swagger.json 的
  response schema 基本是空的，只有 `WithName`/`WithTags` 的路由级信息。
- 路由拓扑：前端支持 router(5001)/engine(5208) 双连接模式（`api.ts:44-46`）；
  Router 是 Yarp 网关，`RouterEndpointExtensions.cs` 的 11 个 `app.Map*` 中大量是**盲转发**
  （如 /conversations 直拼 query 转发，:52-70），这些组合端点不会出现在 Engine 的
  swagger 里。管理面走 `ConfigurationController`（`[Route("api/v1/admin")]`，控制器风格）。

## 2. 方案设计：路线评估与推荐

### 路线 a：OpenAPI（NSwag/Swashbuckle → openapi-typescript）

- 优点：行业标准、工具链成熟、能同时校验端点形状。
- 代价（针对本仓库）：
  1. 要让 minimal API 产出 response schema，需把 `Results.Ok` 全量迁 `TypedResults.Ok`
     并外提返回类型（或逐端点 `Produces<TypedResults.Ok<T>>()` 注解）——
     8 个 EndpointExtensions 文件、几十个 handler 的机械改造，且**每加一个端点都要记得
     注解**，是持续税；
  2. Router 组合/转发端点不在 Engine swagger 内，需要自建合并层或放弃这些路径的类型；
  3. `StreamEvent`（SSE）不是 REST 资源，OpenAPI 表达不了，仍要手写；
  4. 前端镜像的其实是 **Contracts DTO**（ConversationMessage 等），不是端点——
     OpenAPI 生成的是"端点形状"，与痛点错位。

### 路线 b：从 C# Contracts 契约直接导出（推荐）

一次性 dotnet 工具加载 `OpenAgent.Contracts.dll`，反射导出**白名单类型**为 TS：

```text
Backend/tools/OpenAgent.TypeSync/        # net8.0 console，仅引用 Contracts
  Program.cs          # 反射 + System.Text.Json 元数据 → TS AST/文本
  ExportList.cs       # 白名单：ConversationMessage/ConversationRecord/
                      #   LlmInteractionRecord/ContextSummary/TokenUsage/
                      #   AgentSummary/FileAsset/AgentConfigEntity/
                      #   McpConfig/RagConfig/SkillsConfig/LlmProviderProfile/...
  Naming.cs           # camelCase policy（对齐 wire 上 ASP.NET Core 默认 JSON）
```

映射规则（对齐现状 wire 格式，`RedisConversationCache`/HTTP 均为 camelCase 属性）：

| C# | TypeScript |
|----|------------|
| `string?` / 可空引用 | `field?: string` |
| `DateTimeOffset` | `string` |
| 数值枚举（`ConversationStatus`） | `number`（保留现有 `| number` 宽松联合可手写包装） |
| `[JsonConverter(JsonStringEnumConverter)]` 枚举（`ApiFormat`） | 字符串字面量联合 |
| `record`/`class` | `export interface` |
| `required` 成员 | 非可选 |

产出 `Frontend/OpenAgent.Chat/src/types.generated.ts`（**只放 wire 类型**）；
`types.ts` 保留并 re-export：UI-only 类型（`ProcessActivity`/`PendingFile`/
`ToolActivity`/`MessageFile.previewUrl` 等 UI 扩展）与生成类型的本地扩展
（`ConversationMessage & { files?: ... }`）留在手写层。审查建议的 a/b 两条路线中，
推荐 **b**：痛点是"DTO 双侧手改"，b 直接命中、无端点改造税、与 Router 拓扑无关；
`StreamEvent`（SSE，类型取自 `event:` 帧名）留手写层（见风险表）。

## 3. 影响面清单（文件级）

| 文件 | 变化 |
|------|------|
| `Backend/tools/OpenAgent.TypeSync/*` | 新增工具项目（加入 sln，不进任何运行时依赖） |
| `Backend/OpenAgent.sln` | 注册工具项目 |
| `Frontend/OpenAgent.Chat/src/types.generated.ts` | 生成物（提交进库，保证 CI 无 SDK 也能 type-check） |
| `Frontend/OpenAgent.Chat/src/types.ts` | 收缩为 UI 类型 + `export * from './types.generated'`（消费方 import 路径不变） |
| `Frontend/OpenAgent.Chat/package.json` | `"gen:types": "dotnet run --project ../../Backend/tools/OpenAgent.TypeSync"` |
| `.github/workflows/ci.yml` | frontend job 前置一步：生成 → `git diff --exit-code Frontend/OpenAgent.Chat/src/types.generated.ts` |
| `scripts/`（可选） | 包装脚本供本地 pre-commit 使用 |
| `docs/integrations/`（可选） | 一段工具说明 |

Contracts 本身**零改动**；白名单内类型如带 XML doc 可透传为 TS JSDoc 注释。

## 4. 分阶段实施步骤

1. **PR-10a：工具 + 首批类型**。实现 TypeSync（白名单 ≤ 5 个高频漂移类型：
   `ConversationMessage`/`ConversationRecord`/`TokenUsage`/`LlmInteractionRecord`/
   `FileAsset`，均为 Contracts 公开类型）；
   types.ts 切 re-export；前端 `pnpm test`/`pnpm build` 全绿（含 `vue-tsc`）。
2. **PR-10b：全量白名单 + CI 门禁**。扩到约 20 个契约类型；ci.yml 加
   `gen:types && git diff --exit-code` 步骤；README 说明本地工作流。
3. **PR-10c：漂移清偿**。借生成 diff 修掉存量不一致（如补 `traceId`、
   `TokenUsage.cachedInputTokens` 可空性），逐项 review——这是行为面变化，
   单独成 PR 便于回滚。

### CI 门禁形状（PR-10b）

```yaml
# .github/workflows/ci.yml → frontend job，置于 pnpm test 之前
      - name: Set up .NET          # 生成器需要 SDK
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - name: Regenerate contract types
        run: pnpm gen:types
      - name: Fail on drift
        run: git diff --exit-code Frontend/OpenAgent.Chat/src/types.generated.ts
```

要点：生成物**提交进库**（而非 CI 现场生成完丢弃），门禁语义是"改了契约忘了重新生成"——
这样本地无 dotnet 的前端贡献者也能获得完整类型检查，diff 里能直接看到契约变化的 TS 侧影响。

## 5. 风险与回滚

| 风险 | 等级 | 缓解 |
|------|------|------|
| 反射导出的可空性判断不准（NRT 注解在运行时是特性，`string?` vs `string` 需读 `NullableAttribute`） | 高 | 工具内实现 NRT 特性解析并有单测；首批类型人工比对 wire 抓包 |
| `AgentStreamEvent` 已是 Contracts 公开 record（`Requests/AgentStreamEvent.cs:12`），但其 `Type` 是无转换器的数值枚举，wire 上前端 `StreamEvent.type` 实际取自 SSE `event:` 帧名而非序列化字段（`api.ts:273-288`），形状不对齐 | 中 | `StreamEvent` 留手写层；或给枚举补 `JsonStringEnumConverter` 后再入白名单，属独立小改动 |
| 命名 policy 漂移：个别端点/缓存自定义序列化（`RedisConversationCache` camelCase、EF 默认）与生成默认不一致 | 中 | 生成规则锚定 HTTP wire（camelCase）；PR-8 落地后序列化语义统一，双重保障 |
| 生成文件冲突/噪音（排序、换行）导致 diff 门禁误报 | 中 | 输出确定性排序（成员按字母）、固定换行/缩进、`--check` 模式下先 format 再 diff |
| 双 JSON 形态（`AuthTokenResponse` 的 snake_case，`types.ts:266-271`）不在 camelCase 规则内 | 低 | 该类型留手写层，不入白名单 |
| CI 需 dotnet（frontend job 目前只有 Node） | 低 | frontend job 加 setup-dotnet 8.0.x 步骤，耗时秒级 |

回滚：生成层是"叠加物"——revert 后 types.ts 恢复全手写即可；不触任何后端运行时行为。

## 6. 验收标准

1. `pnpm gen:types` 幂等：连续两次运行 `git status` 干净。
2. ci.yml 门禁生效：人为改一个 C# 契约字段（演练），CI 的
   `git diff --exit-code` 步骤失败并指出 types.generated.ts 过期。
3. 白名单类型不再存在于手写 types.ts（重复定义被 lint 级检查或 review 约定拦截）。
4. `vue-tsc --noEmit` 通过；既有 vitest（api.test.ts 等依赖类型的 17 处 import）零改动
   或仅 import 路径不变式适配。
5. 后端 `dotnet build` 不受影响：TypeSync 为独立工具项目，运行时项目依赖图无变化
   （Contracts 除外，且仅被工具引用）。
6. 演练记录：在 `ConversationMessage` 加一个字段，前端类型在 `pnpm gen:types` 后一 步到位，
   无手工镜像编辑（PR 描述留证）。
