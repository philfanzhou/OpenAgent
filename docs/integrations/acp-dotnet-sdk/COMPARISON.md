# 官方与社区 ACP SDK 架构对比

> 2026-10-16 复核官网与 11 个源码仓库；本次为静态架构/接口核对，没有运行这些库的完整测试。固定提交和关键文件见 [architecture-sources.json](./architecture-sources.json)。社区范围重点覆盖 .NET 三项，另选 Go、React 作为不同层次的代表，不声称遍历全部社区语言库。

## 官方目录与共同边界

官网官方库导航列出 TypeScript、Rust、Python、Java、Kotlin；.NET 的 acp-csharp、Acp.Net、Agentic.ACPLibrary 位于 [Community](https://agentclientprotocol.com/libraries/community)。官网收录不等同于官方维护、完整合规认证或生产成熟度背书。

综合源码可归纳出共同层次：**协议模型/校验 → Transport → 双向 RPC/请求关联 → Client/Agent 与 Session → 宿主业务**。这是本次归纳，不代表每个库都按五个独立包发布；Process、UI 状态、框架托管、Proxy 各有单独扩展层。

SDK 提供通信、handler 与数据结构；模型推理、历史持久化、文件执行策略和审批 UI 由宿主实现。传入 MCP 配置、支持工具进度消息，也不等于 SDK 自动运行完整工具系统。

## 五种官方 SDK

| SDK | 实际架构入口 | 已提供的功能/扩展层 | 对 C# 的参考价值 |
|---|---|---|---|
| [TypeScript](https://github.com/agentclientprotocol/typescript-sdk) | 生成 types/Zod/guards；jsonrpc.Connection；stream；client()/agent() builder；Context、SessionBuilder、ActiveSession | 双侧强类型 request/notification、会话更新、权限/文件/终端/elicitation 路由、请求取消、会话便捷层；另有 HTTP/SSE、WS；v2 使用独立 experimental 入口 | 学习 API 分层、handler 注册、会话消息归属和生成模型；避免从旧 ClientSideConnection 示例重新起步 |
| [Rust](https://github.com/agentclientprotocol/rust-sdk) | 独立 schema crate；Role/Builder/ConnectionTo/Dispatch；Session；stdio/process feature | Client、Agent、Proxy、Conductor；连接与会话；HTTP/WS 独立 crate；rmcp 接入、polyfill、trace viewer、cookbook；v2/MCP-over-ACP 等显式试验特性 | 学习核心与 process/传输/代理链的边界；首版 C# 可只做 Client/Agent，延后代理编排 |
| [Python](https://github.com/agentclientprotocol/python-sdk) | 生成 Pydantic 模型；Agent/Client 接口与 Router；Connection；TaskSupervisor/MessageSender；transport | asyncio 双向请求、取消传播、断线清理、stdio/HTTP/WS；contrib 的 SessionAccumulator、ToolCallTracker、PermissionBroker；experimental/v2 | 学习异步任务生命周期，以及将 UI 状态累积器和权限辅助放到可选层；Accumulator 自身标记 experimental |
| [Java](https://github.com/agentclientprotocol/java-sdk) | acp-core；Client/Agent 的 sync/async API；spec/session/transport；NegotiatedCapabilities；可替换 JSON mapper | 双侧协议调用、能力推导与校验、更新及反向请求；独立 Jackson 2/3、注解 Agent、Servlet/WebFlux/Jetty、测试模块；跨语言测试工程 | 学习包边界、能力与 handler 一致性、框架托管适配及互操作门禁；本次不据其 README 宣称 v2 已验证 |
| [Kotlin](https://github.com/agentclientprotocol/kotlin-sdk) | acp-model；Protocol + 协程 scope；Agent/Client runtime；AgentSupport/AgentSession、ClientSessionOperations；acp-ktor* | 生命周期、流式消息、权限/文件/终端等接口；stdio、Ktor HTTP/WS；通知排序、早到 session 更新处理；独立 v2 类型/运行时和协商示例 | 学习取消与结构化并发、每会话 handler、会话建立期间的通知路由 |

这些是源码存在的结构与入口，不能合并推导成“每个 SDK 每个新能力都已经通过互操作”。例如 Python 的可选累积器只覆盖其支持的常见更新，不能因为模型齐全就假设所有更新都有完整 UI 状态归并。

## 功能应分三层理解

| 层次 | 典型功能 | 完成责任 |
|---|---|---|
| 协议面 | initialize/authenticate、会话 new/load/list/resume/close/delete、prompt/update/cancel、配置/模式、内容块与错误 | SDK 提供类型、调用与分派；具体可用项需能力协商，跟进程度按库检查 |
| 交互面 | 权限请求、文件读写、终端生命周期、elicitation、计划/工具/命令/用量/压缩等更新 | SDK 传输与路由；Client/Agent handler 实现真实操作或 UI |
| 工程面 | 并发请求表、入站调度、顺序保证、取消、流控、EOF/进程清理、日志、schema 生成和互操作夹具 | 成熟 SDK runtime/测试层；不只是声明 DTO |

官网 [v1 传输](https://agentclientprotocol.com/protocol/v1/transports) 仍将远程传输标为草案；SDK 已有 HTTP/WS 代码和协议正式稳定是两个不同状态。[v2](https://agentclientprotocol.com/protocol/v2/migration) 仍为草案，且改变 prompt 完成、能力、回放和工具更新语义，不能只修改协议整数。

## 官网收录的 .NET 三项

| 项目 | 架构与已实现接口 | 当前缺口/限制 |
|---|---|---|
| [nuskey8/acp-csharp](https://github.com/nuskey8/acp-csharp) | 单包 AgentClientProtocol；Schema + STJ source context → 自有 JsonRpcEndpoint → ClientSideConnection/AgentSideConnection；IAcpAgent/IAcpClient 双侧；初始化、认证、新建、prompt、取消、load、mode、旧 model 方法、权限/文件/五个终端方法、泛型扩展 | 当前 IAcpAgent 未列 list/resume/close/delete/set_config_option；接口层未见 elicitation；主要由消费者提供 reader/writer；多目标含 net8.0/netstandard2.1；不能由序列化 context 推断全链路 AOT 已验证 |
| [AgenticDesktop/ACPLibrary](https://github.com/AgenticDesktop/ACPLibrary) | 单程序集内分 Models/JsonRpc/Protocol/Transport/Client/Agent；RequestTracker 关联请求；dispatcher 注册 handler；Client/Agent、StdioAgentTransport 启动子进程、StdioHostTransport 提供 Agent stdio；DI、MockAgent、权限/文件/终端 handler、事件与扩展注册 | Client 有 LoadSessionAsync；内置 Agent handler 仅 initialize/new/prompt/cancel，不能将 Client 能调用 load 等同 Agent 已实现 load；当前主库 net10.0；未见现代完整会话方法集与 config/elicitation 内置接口 |
| [MertBasar0/acp-net](https://github.com/MertBasar0/acp-net) | Acp.Net.Process（net8.0）+ Acp.Net.Testing；Runner → RuntimeResolver/Preflight → ProcessSession；原生/WSL、路径/环境处理、关闭策略、transcript/run artifact、确定性假 Agent | 不拥有 ACP DTO、请求分派或完整 Client/Agent；应与协议库组合，不能替代协议库 |

两种 .NET 协议库都存在读取循环等待 handler 的代码路径。**[推断，未复现]** 长 prompt 内等待反向文件/权限响应，或等待取消时，需要专门验证读循环是否被阻塞；提供 async 方法本身不保证双向调用不会死锁。应先做探针验证再决定复用，不据此宣称所有场景都失败。

## 其他有代表性的社区分层

| 项目 | 定位与架构 | 已提供功能 |
|---|---|---|
| [coder/acp-go-sdk](https://github.com/coder/acp-go-sdk) | schema/meta → 生成 types/constants/helpers/双侧 dispatcher → 共用 Connection → ClientSideConnection/AgentSideConnection | 请求关联、context 取消、入站请求独立处理、有界有序通知队列、响应 notification watermark/barrier、扩展方法、JSON 对照夹具；适合借鉴 runtime 正确性 |
| [marimo-team/use-acp](https://github.com/marimo-team/use-acp) | 官方 TypeScript SDK → WebSocket manager → AcpClient → React hook + Zustand store → UI | 重连、多连接/会话状态、通知时间线、权限交互、slash commands、session modes；它属于 Client/UI 层，不提供 Agent 推理服务 |

浏览器层使用 WebSocket 需要对应服务端/桥接；不能直接用浏览器访问本地 stdio，也不能把 UI hook 当作一套完整服务端 SDK。

## 对当前 C# 工作的建议

1. 公共 API 参照 TypeScript 的 Client/Agent + Session/Context，运行机制参考 Python/Kotlin，模块与互操作测试参考 Java。
2. 代码生成采用固定的官方稳定 schema；不要将某个 SDK 的全部试验类型当成稳定协议，沿用 [PROTOCOL.md](./PROTOCOL.md) 的边界。
3. 按本轮单包要求，Protocol、Transport/Rpc、Client、Agent、Processes 作为 OpenAgent.Runtime.Acp 内部模块；测试辅助与测试控制台放非发布工程，进程层可比较 Acp.Net。
4. 当前 Demo 已有 stdio、基本 RPC、Echo Agent、外部启动、模型选择展示和安全默认权限响应；仍是单请求客户端，尚无完整强类型模型、可重入并发 RPC、会话恢复、完整配置与工具 handler。
5. 下一步优先验证并发/反向调用/取消/EOF、再补会话与配置；增加工具执行前落实能力与 handler 一致性。此处是建议，不表示已实现或已排期。

本轮只新增对比资料，不修改 Demo 的执行逻辑。已有规格见 [SPEC.md](./SPEC.md)；旧调研保留原日期，避免把本次复核与之前的静态快照混为同一次验证。
