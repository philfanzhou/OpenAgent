# ADR-0004：Router 重新定位为面向任意下游的 Agent 网关

## 状态

> **已被独立仓库取代（2026-10-16）。** Router 以 `philfanzhou/OpenAgent.Router` 为准，它保留有状态会话绑定、限流、幂等、Router 侧 ACL 和 Gina，与本文的无状态网关、`HttpProvider` 统一、四项目拆分、`/v1/decisions` 决策模型方向冲突，这些部分搁置，仅作历史记录。仍然有效的只有两项：HTTP 注册端点（已移植到独立仓库，共享密钥）和 Engine 侧 Agent 白名单（`AgentConfig.Allowed*`）。分支 `refactor/router-slim` 不合并进 `main`。

草案。需求已对齐；迁移顺序第 1 至 5 步已落地（见文末「实现进度」），第 6 步尚未开始。标注 `[待决策]` 的事项确认后转为已决策。

## 背景

Router 原本是 Engine 的前置网关，并混有幂等、响应缓存、限流和 Redis 直读的 Agent ACL。第一轮瘦身（分支 `refactor/router-slim`）已移除这些模块，Router 只保留转发、会话亲和、意图选择和服务发现。

现状仍有结构问题：

- `Endpoints`、`Routing`、`Providers`、`Security` 在同一个项目里，Gina 作为带专属协议翻译的 Provider 与 Engine 混在同一层。
- Router 与 Engine 各自写了一份 Redis 键字符串（`agent:config:*`、`engine:registry:*` 等），没有共享定义。
- 下游被假定为 Engine，没有对外发布的下游标准。
- 瘦身后 Agent 可见性没有任何过滤：Engine 侧没有 ACL 实现，所有已认证用户可选到所有 Agent。

## 决策

### 1. 定位

Router 是面向任意下游的 Agent 网关。下游实现一份 HTTP + SSE 的 OpenAPI 契约即可接入，Engine 是该契约的默认实现，其他下游（包括 Gina）自行实现同一契约。

Router 负责：转发、会话与任务亲和、意图选择、多实例路由。Router 不存业务数据、不验证身份、不做授权。

### 2. 已对齐的需求

| 事项 | 决定 |
|------|------|
| 下游范围 | 不限类型，由标准契约约束 |
| 通信形态 | HTTP + SSE，OpenAPI 描述 |
| 发现方式 | 下游主动注册加心跳，同时支持静态地址配置；多实例动态扩缩 |
| 身份 | Router 透传 `Authorization`，下游自行验证并解析用户与租户 |
| 可见性 | 下游在目录接口按调用者过滤，Router 不保存权限数据 |
| 意图选择 | 支持多种策略，按配置顺序尝试，超时或低置信度跳过 |
| 契约范围 | 聊天、Run、Agent 目录、会话、文件、`/me`、注册、健康检查 |

Router 读取 token 中的租户仅用于路由分片。该值未经验签，不得用于任何授权判断，下游必须自行校验。

### 3. 分层架构

```text
客户端层    Chat 前端 · 第三方客户端 · 管理端（仅开发）
              │  HTTPS + Authorization
网关层      OpenAgent.Router
              ├─ Router.Host        认证透传、端点、YARP 转发、健康检查、遥测，唯一 DI 组装点
              ├─ Router.Providers  HttpProvider、StaticDirectory、RedisRegistryDirectory
              ├─ Router.Core       选择管线、IIntentStrategy、亲和规则、目录与路由抽象
              └─ Router.Contracts  OpenAPI v1、模型、注册 JSON、Redis 键
  ─ ─ ─ ─ 下游标准契约 v1 · HTTP + SSE ─ ─ ─ ─
下游层      Engine（默认实现）· 其他实现标准契约的下游
基础设施层  PostgreSQL · 对象存储 · LLM/MCP/RAG · Redis（Router 唯一直连）
```

依赖方向自上而下，上层引用下层，反向引用由架构测试禁止。

- Router 无状态；会话与 runId 亲和存在可丢失的缓存里，丢失后向下游确认归属。
- 只有 `HttpProvider` 一种 Provider 实现；下游必须直接提供标准接口，Router 不为单个下游做适配，Gina 需自行实现契约。
- Redis 键名与注册 JSON 只在 `Router.Contracts` 定义一次。

契约细节见 [Router 下游契约 v1](../modules/router/downstream-contract.md)。

### 4. 选择管线与意图策略

选择顺序：显式 Agent → 会话或 runId 亲和 → 意图策略链 → Fallback → 路由与转发。

```csharp
public interface IIntentStrategy
{
    string Name { get; }

    // 返回 null 表示本策略无结论，管线继续尝试下一个。
    Task<IntentSelection?> SelectAsync(
        IntentRequest request,
        IReadOnlyList<AgentSummary> candidates,
        CancellationToken cancellationToken);
}
```

首批内置，按此顺序尝试，第一个有结论的生效：

- `ModelIntentStrategy`：调用决策模型服务 `POST /v1/decisions`。把候选 Agent 作为一道选择题（`type: choice`，`criteria` 的键是 `agentId`，值是名称与描述），另加一个 `__none__` 选项让模型可以弃权。响应取 `answers.agent.choice` 与 `confidence`，低于 `MinimumConfidence`、选了非候选项、选了 `__none__`、超时或服务出错都视为无结论，交给下一个策略。未配置 `Endpoint` 时整个策略跳过。
- `AgentIntentStrategy`：调用 `intent-router` Agent，即现状。`[待决策]` 模型稳定后是否保留作后备。
- `FallbackStrategy`：配置的默认 Agent。

决策模型的配置在 `RouterSettings:IntentRecognition:DecisionModel`：`Endpoint`（为空则关闭）、`TimeoutMs`（默认 3000）、`MinimumConfidence`（默认 0.5）、`Headers`（若服务需要鉴权，密钥通过环境变量注入）。仓库内不提交具体地址。服务端点是明文 HTTP，部署前需确认 Router 与它之间的网络边界，用户消息会发送给它。`[待确认]` 服务是否需要鉴权，以及示例中没有出现的模型名参数是否需要传递；尚未用真实服务联调。

### 5. 长事务

长事务的问题是连接断开后任务丢失，而不是传输协议。契约区分两种交互：

- 短交互：`POST /api/v1/agent/chat` 与 `/chat/stream`，保持现状。
- 长事务：Run。`POST /api/v1/agent/runs` 立即返回 `runId`，事件通过 `GET /runs/{id}/events` 以 SSE 推送并支持 `Last-Event-ID` 续传，补充输入与取消用独立 POST。

任务状态与事件由下游持久化，Router 只做 runId 亲和与事件流保活。

**不引入 WebSocket。** 取消、补充输入和审批都可以用单独的 POST 完成，保留 HTTP 的鉴权、状态码和幂等语义；WebSocket 只在握手时验证身份，会使契约变成 OpenAPI 无法描述的自定义消息协议。出现实时双向场景（语音、协同编辑、客户端高频回传工具结果）时重新评估，届时作为同一 Run 事件的另一种传输加入，YARP 支持代理 WebSocket。

### 6. 语言与运行时 `[待决策]`

下游契约是 OpenAPI，下游语言不受限，决策只影响 Router 自身。

| 维度 | 路线 A：.NET 10 LTS | 路线 B：Go |
|------|--------------------|-----------|
| 做法 | 原地升级运行时，沿用四项目结构与 YARP | 用 Go 新写，与现有 C# 版并行运行后切换 |
| 转发与 SSE | YARP 功能完整 | `httputil.ReverseProxy` 加 `FlushInterval: -1`；健康与隔离自写 |
| 部署形态 | 镜像百 MB 级，启动百毫秒级 | 单个静态二进制，镜像几十 MB，启动毫秒级 |
| 契约与 Engine | 与 Engine 共用 Contracts 项目 | 模型由 OpenAPI 生成，Redis 键另行同步，需契约测试 |
| 改动量 | 小 | 大：重写约 4000 行及全部测试，并存期间两套 CI |
| 迁移风险 | 低 | 中高，需要流量对比与回滚开关 |

选路线 A 的前提：Engine 仍是 C# 并希望共用 Contracts；团队无 Go 生产经验；镜像、启动与内存没有成为问题。

选路线 B 的前提：Router 要作为与 Engine 无关的独立产品；扩缩容频繁或节点资源紧张；团队有 Go 经验并愿意维护两套流水线。

倾向：纯技术上 Go 略占优，优势在部署形态而非功能；默认选路线 A，满足路线 B 前提时才选 B。.NET 8 的官方支持据信在 2026 年 11 月结束 `[待确认]`，请以微软官方支持政策为准，这决定路线 A 的升级时点。

## 影响

| 对象 | 改动 | 工作量 |
|------|------|--------|
| Router | 拆为四个项目；搬迁现有代码（约七成可复用）；新增注册端点与策略链 | 中 |
| Engine | 启动时注册并续约；目录接口按用户过滤；读取标准请求头；实现 Run 的持久化、事件重放与实例间接管；契约测试 | 最大 |
| Gina | 自行实现标准契约；Router 内的 `GinaProvider` 已删除，在其实现契约前不可接入 | 中 |
| 前端 | 路径不变；需确认是否依赖已移除的缓存与幂等行为 | 小 |
| 架构测试 | 新增四个项目的引用规则；保留 Router 不得引用 Core 与 Engine 的约束 | 小 |

## 迁移顺序

1. 建 Contracts 项目：OpenAPI、模型、Redis 键。Engine 与 Router 同时引用。不改变行为。
2. 抽出 Core：选择管线与策略接口，现有意图逻辑封装为 `AgentIntentStrategy`。不改变行为。
3. 抽出 Providers：合并为 `HttpProvider`，删除 Gina 专属代码。（已完成）
4. 加注册端点与静态目录，Engine 接入注册；旧的 `engine:registry:*` 键保留一个版本做兼容。（已完成）
5. Engine 补目录 ACL 过滤与契约测试，随后关闭兼容键。（ACL 已完成，契约测试与关闭兼容键未做）
6. 加 Run 契约：Engine 先实现任务持久化与事件重放，Router 加 runId 亲和与事件流保活。依赖第 5 步。

路线 B 的差异：第 1 步之后，用 Go 实现契约最小子集（chat/stream、agents、registrations），与 C# Router 并行跑同一份契约测试，按租户或流量比例切换并保留回滚开关，通过后再补会话、文件、Run。路线 A 在第 1 步前增加第 0 步：升级到 .NET 10。

## 风险与待决策

1. 注册接口用共享密钥鉴权（已决策）：请求头 `X-Registration-Key`，Router 配置 `RouterSettings:Registration:SharedSecret`，未配置时不暴露注册端点。共享密钥是整个集群一把，泄露后需同时轮换 Router 与所有下游；下游增多或需要按实例吊销时再换 mTLS。
2. `[待决策]` Agent ID 是否改为 `providerId/agentId` 命名空间。现状遇重复返回 409，下游增多后冲突概率上升。
3. `[待确认]` 第三方下游是否都能接入同一身份提供方。做不到时 Router 无法替其兜底。
4. Engine 已有租户隔离，并在目录与执行入口按 `AgentConfig.AllowedUserIds/AllowedGroups/AllowedRoles` 过滤（迁移第 5 步，已完成）；三个列表全空表示租户内所有用户可用。存量 Agent 配置没有这些字段，行为不变。
5. `[待决策]` Run 事件保留期。草案默认 24 小时，由下游在注册时声明。
6. 风险：任务要在实例缩容后继续，Engine 的执行不能只存在内存里，这是 Engine 侧最重的改造，需单独评估。
7. 契约演进：v1 发布后字段只增不改，破坏性变更走 v2，Router 通过 `X-OpenAgent-Contract` 同时支持两个主版本。
8. 验证环境：本机仅有 .NET SDK 10，而 `global.json` 固定 8.0.100，架构测试在此环境无法编译，落地前需在 SDK 8 或升级后的环境跑全量测试。

## 实现进度

已落地（行为不变，仅结构调整）：

- Router 拆为 `Router.Contracts`、`Router.Core`、`Router.Providers`、`Router.Host` 四个项目；`Router.Core` 不使用 ASP.NET Core 服务端、Yarp、Redis 类型，由架构测试守住。
- Engine 注册条目与 Redis 键收敛为 `Router.Contracts` 中的 `EngineRegistryEntry` 与 `EngineRegistryKeys`，Engine 与 Router 共用，不再各写一份。
- 删除瘦身后遗留的 7 条无人调用的日志定义。
- 统一 Provider：只保留 `HttpProvider`，路径固定在 `DownstreamPaths`；删除 `GinaProvider`、两个 Provider 工厂、`IAgentProviderFactory` 与 `IAgentProvider.ConfigureRequestAsync`。Provider 配置简化为 `Id`、`BaseUrl`、`ServiceHeaders`（破坏性变更，见 agent-provider.md）。
- `IIntentStrategy` 策略链：`AgentIntentStrategy`（现状）与 `FallbackStrategy` 按注册顺序依次尝试，第一个有结论的生效。策略顺序暂不开放配置，等出现第二个真实策略（如决策模型）再加。
- 下游契约 OpenAPI 文件：`Router.Contracts/openapi/downstream-v1.yaml`，已实现端点按 Engine 现有代码整理，Run 端点标为提议。

- `ModelIntentStrategy`：按决策模型的 `/v1/decisions` 接口实现，默认关闭，配置 `DecisionModel:Endpoint` 后排在策略链首位。

- 注册端点（迁移第 4 步）：Router 提供 `PUT/DELETE /api/v1/router/registrations/{instanceId}`，共享密钥鉴权，写入原有 `engine:registry:*` 键，因此直连 Redis 的旧 Engine 与 HTTP 注册的实例在同一个池里，兼容键无需另设迁移。Engine 侧的 HTTP 注册客户端已撤掉，Engine 仍直写 Redis。静态地址沿用 Provider 的 `BaseUrl` 与路由表的静态回退，没有新增机制。
- Agent ACL（迁移第 5 步）：`AgentAccess.IsAllowed` 是唯一规则，目录接口和 `AgentRuntimeResolver` 都调用它，所以隐藏的 Agent 也不能靠显式 `X-Agent-Id` 执行。
- ACL 同步：保存 Agent 配置时，Engine 在同一个 Redis 事务里把 `Allowed*` 写入 `agent:acl:{agentId}`（三个列表全空则删除该键），独立仓库的 Router 读这个键做目录过滤。此前没有任何代码写入该键，Router 侧 ACL 实际上一直全部放行。

未落地：关闭 Redis 直写的兼容路径（待所有 Engine 切到 HTTP 注册后）、注册 `providerId` 与 `contractVersion` 字段、Run 契约的实现、契约测试。

## 关键实现

- `Backend/src/OpenAgent.Router.{Contracts,Core,Providers,Host}/`
- `Backend/tests/OpenAgent.Architecture.Tests/AssemblyDependencyTests.cs`（四个项目的引用规则）
