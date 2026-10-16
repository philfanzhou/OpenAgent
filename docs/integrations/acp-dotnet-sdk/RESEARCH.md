# ACP .NET SDK 调研

> 2026-10-15；一手资料与源码静态核对。未安装被调研项目的依赖，未运行其测试，也未使用付费模型做实机联调。固定源码位置见 [SOURCES.md](./SOURCES.md)。以下“建议”是调研时的设计判断；本轮工程组织已统一为 OpenAgent.Runtime.Acp 单包，以 SPEC.md 为准。

## 1. 关键结论

1. **首版实现稳定 ACP v1，v2 独立试验。** 官网同时发布 v1 Latest、v2 Draft；SDK 包版本、schema 包版本和握手里的协议版本是三个不同概念。v1 握手用整数 `1`。[初始化](https://agentclientprotocol.com/protocol/v1/initialization)、[v2 迁移](https://agentclientprotocol.com/protocol/v2/migration)。
2. **必须同时考虑 Client 和 Agent 两侧。** Client 可以是 IDE、桌面应用或后端宿主；Agent 接受会话及提示，也会反向请求 Client 的权限、文件与终端操作。ACP 不是单向聊天 HTTP API。[架构](https://agentclientprotocol.com/get-started/architecture)。
3. **稳定基线优先 stdio。** ACP stdio 是 UTF-8、每行一个 JSON-RPC 消息，诊断走 stderr。官网仍将 Streamable HTTP 标为草案；已有 SDK 的 HTTP/WebSocket 代码可作后续参考。[传输](https://agentclientprotocol.com/protocol/v1/transports)。
4. **.NET 已有社区实现，应先比较再决定复用。** 官网将 .NET 列入 Community，而官方语言库导航列出 TypeScript、Rust、Python、Java、Kotlin。本次未发现官网列出的官方 .NET SDK；这是本次目录核对结果，不代表全网不存在。[库目录](https://agentclientprotocol.com/libraries/community)。
5. **建议新 SDK 的价值放在完整性与可验证性。** 自动跟踪稳定 schema、双向调用、可确认的取消、会话恢复、AOT 证据和跨语言测试，比再次手写一组 DTO 更有价值。

## 2. 官方协议基线

本次固定协议仓库提交 `1cebe1c01837379abf34dd62b71e9c75bf140669`：

| 项目 | 实测静态结果 | 对设计的影响 |
|---|---|---|
| `schema/v1/schema.json` | 178 个 `$defs` | 协议模型应从稳定 schema 生成并复核 |
| `schema/v1/meta.json` | 25 个方法名：Agent 侧 13、Client 侧 11、通用 1 | 同时覆盖 request 与 notification，不能当作 25 个请求 |
| `SessionUpdate` | 14 种已知分支 | 不只支持文本和工具调用 |
| 稳定 v1 新能力 | resume、close、delete、elicitation、boolean config、notice、compaction 等已在稳定 schema 中 | 不按早期博客冻结范围；逐项按能力协商 |
| `schema/v1/schema.unstable.json` | 单独存在 | fork、providers、subagents 等试验能力不得混入稳定承诺 |

这些数量是指定提交的文件统计，不是协议永久常量；完整哈希和方法清单在 [sources.json](./sources.json)。

### 实际发现：官方 SDK 的 schema 也有不同步和试验内容

| 源文件 | `$defs` 数量 | 核对发现 |
|---|---:|---|
| 协议仓库稳定 v1 | 178 | 本规格唯一生成基线 |
| TypeScript `schema/schema.json` | 277 | 含 `session/fork`、`providers/*`、`nes/*` 等额外方法 |
| Python `schema/schema.json` | 268 | 同样包含稳定基线以外的方法 |
| TCK 自带 v1 schema | 170 | 与本次协议 schema 哈希不同 |

**建议：** 使用协议仓库的稳定 schema + meta 生成 DTO 和方法目录；官方 SDK 是运行机制与互操作参考。不能直接复制某个 SDK 的“全部接口”后宣称稳定 v1 全覆盖。定义数量差异本身也不能证明某个 SDK 有缺陷。

## 3. 五种官方 SDK 的参考价值

| SDK | 本次核对的文件/案例 | 值得借鉴 | .NET 设计判断 |
|---|---|---|---|
| [TypeScript](https://github.com/agentclientprotocol/typescript-sdk) | `src/acp.ts`、`jsonrpc.ts`、`stream.ts`、`schema-deserialize.ts`、`src/examples/client.ts` | 新 builder/handler API、NDJSON、双向请求、协作取消、流量上限；稳定与试验 v2 入口分开 | 用强类型协议层加会话便捷层；保留原始事件；借鉴输入容错，不照搬 JS API |
| [Rust](https://github.com/agentclientprotocol/rust-sdk) | 根 README、`src/agent-client-protocol/src/stdio.rs`、workspace Cargo 配置 | Client/Agent/Proxy 角色、连接构造、schema 独立依赖；v2 与 MCP-over-ACP 显式试验开关 | 首版只做 Client/Agent；保留可替换 transport，延后 Proxy/Conductor |
| [Python](https://github.com/agentclientprotocol/python-sdk) | 根 README、`examples/agent.py`、`src/acp/` 及 schema | 生成模型、异步 Agent 基类、可运行 echo 示例、contrib 辅助层 | 最小 echo agent 应无需任何模型账户；状态累积器放可选层 |
| [Java](https://github.com/agentclientprotocol/java-sdk) | 根 README、`integration-testing/README.md` | 同时提供 Client/Agent；以独立进程做跨 SDK 场景和负载检查 | 优先借鉴测试矩阵；.NET 用 Task，不引入阻塞/响应式三套 API |
| [Kotlin](https://github.com/agentclientprotocol/kotlin-sdk) | 模块表、`ClientHangingSessionUpdateDeliveryTest.kt` | model/runtime/transport 分层；会话建立期间的通知路由回归用例 | 必须处理响应前先到达的会话更新，不能简单按“当前 prompt”转发 |

本次源码中的 TypeScript `package.json` 为 `1.8.0`、Rust workspace 核心包为 `3.3.0`、Python 为 `0.12.1`。它们仅用于识别读取的源码，**未核实对应版本是否已发布到包仓库，也不作为依赖选版结论**。

## 4. 实际案例

### Codex ACP 适配器

[agentclientprotocol/codex-acp](https://github.com/agentclientprotocol/codex-acp) 将 Codex 能力转换为 ACP。`src/AcpAgentRouter.ts` 根据首次初始化选择 v1/v2 handler；`CodexAcpClient.ts` 则连接 Codex 自身的 app-server 协议。

**启示：** 通用 ACP SDK 与具体 Agent 适配器应分离。Agent 特有认证、模型、工具报告和私有扩展不能成为 SDK 核心依赖。源码还包含对某版 SDK 省略 `params` 行为的兼容处理，说明应将“字段省略”纳入自己的互操作用例；这不等于本次验证了该问题仍存在于最新发布版。

### Claude Agent ACP 适配器

[agentclientprotocol/claude-agent-acp](https://github.com/agentclientprotocol/claude-agent-acp) 在 `src/acp-agent.ts` 区分 `resumeSession` 和 `loadSession`，后者会回放历史；还将权限、终端和具体运行时事件转换为 ACP 消息。

**启示：** SDK 提供协议操作与上下文；适配器负责真实会话持久化、执行循环和事件映射。其 AIR、子 Agent、goal 等扩展应按各自文档协商，不能因为案例支持就纳入稳定 SDK 默认行为。

这两个仓库均是 ACP 组织下的适配实现；本资料不把它们称为 OpenAI 或 Anthropic 发布的 ACP 官方 SDK，也未进行实际模型调用。

## 5. 社区 .NET 实现

| 项目 | 本次固定源码观察 | 可复用思路 | 采用前需要补证 |
|---|---|---|---|
| [nuskey8/acp-csharp](https://github.com/nuskey8/acp-csharp) | `AgentClientProtocol`；MIT；多目标包含 net8.0/netstandard2.1；有 Client/Agent、STJ source context。固定 HEAD 日期为 2025-11-05 | 双侧连接和 C# 模型组织 | 本次 `IAcpAgent.cs` 未列当前稳定的 list/resume/close/delete/config-option 方法；需 schema 差异与并发取消核查。未实测 AOT |
| [MertBasar0/acp-net](https://github.com/MertBasar0/acp-net) | Apache-2.0；README 明确定位 Process/Testing 产品族，不拥有完整 ACP 协议类型 | 进程预检、WSL 路径、transcript、关闭策略、fake agent | 不能用它替代协议 SDK；运行记录还需业务侧脱敏 |
| [AgenticDesktop/ACPLibrary](https://github.com/AgenticDesktop/ACPLibrary) | MIT；Client/Agent、DI、MockAgent；当前主库 `net10.0`，包 ID 为 `ShihaoShen.Agentic.ACPLibrary` | 接入体验、回调接口、mock 案例 | 当前目标框架不能直接用于本仓库 net8.0；应核对稳定 schema 与资源生命周期 |

**建议结论：** SDK 保持协议职责分层，按本轮要求在单个项目/包内实现；将以上项目作为比较对象和可选复用候选，暂不直接指定其中一个为基础库。实现前以小型协议探针比较成熟 RPC 库和自有最小实现，再记录选型证据。保留复用许可与 NOTICE；不把项目名称或 NuGet 自述当作兼容性证明。

## 6. JSON-RPC 与序列化选型

[Microsoft StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc/blob/main/src/StreamJsonRpc/NewLineDelimitedMessageHandler.cs) 已有 `NewLineDelimitedMessageHandler`，可作为实现候选。需要验证 ACP 的 `$/cancel_request` 命名、双向调度、ID、通知顺序、缺省字段和 AOT；不能套用默认 header framing 后就认为兼容 ACP。

[System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation) 可减少运行时反射依赖。**建议：** 协议模型与 serializer 先支持 source generation；运行时全链路是否 AOT 兼容，必须由实际发布及互操作测试确认。

## 7. 与 Agent.Matrix 的关系

本次工作区为 `main`，提交 `990f14ca5b563fe81a9a81a5bc4aa053a9d28159`。对 `Backend/src`、`docs/overview`、`docs/integrations` 的本次检索未找到 ACP 实现；不据此推断其他分支不存在实现。

现有 [开发指南](../../overview/DevelopmentGuide.md) 规定 Contracts 不放 SDK 类型，Execution 不引用具体能力实现。因此建议：SDK 不依赖 OpenAgent；平台消费侧负责 ACP session 与平台 conversation、权限和工具事件的映射。若接入另一分支上的 Runtime Adapter，应先核对该分支真实接口，避免依照旧设计新增重复抽象。

研究范围与剩余技术不确定性已转成 [验证要求](./VALIDATION.md)，不表示这些能力已完成。
