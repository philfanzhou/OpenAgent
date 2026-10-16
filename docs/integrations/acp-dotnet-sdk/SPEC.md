# C# ACP SDK 设计规格 v0.3（单包）

> 单包设计 · 2026-10-16。按本轮要求替代 v0.2 的五包方案。所有 SDK 功能收于 `Backend/src/OpenAgent.Runtime.Acp`，只产出一个 SDK 包。本文“必须”除 `[协议]` 外表示 SDK 产品要求。除已落地的 Stream 传输基线外，公共 API 仍为待实现提案。

本文是功能和行为契约入口；[架构设计](./DESIGN.md) 定义同包模块协作，[目录设计](./DIRECTORY.md) 定义工程归属，[分阶段任务](./IMPLEMENTATION.md) 定义每一步的 SDK / 测试 / 控制台交付，[测试控制台](./TEST-CONSOLE.md) 定义使用及演进要求，[协议覆盖](./PROTOCOL.md) 与 [验收规格](./VALIDATION.md) 定义协议和发布门槛。任务状态只在 `docs/planning/TODO.md` 维护。

原 [Demo](./DEMO.md) 保留为历史回归参考，不约束新目录。新测试控制台只调用 SDK；已有传输基线不等于完整 RPC、Client/Agent 或真实模型互操作已经完成。

## 1. 产品目标与边界

提供单一可发布、可复用的 .NET SDK，使开发者能够：

- 在 IDE、桌面程序、CLI、后台 Worker 中连接 ACP Agent。
- 将自己的 .NET Agent 暴露为 ACP Agent，供其他 ACP Client 使用。
- 使用稳定、强类型、可取消的异步 API，接收完整协议事件。
- 扩展传输和私有方法，同时保留协议兼容与能力协商。

SDK 负责通信和生命周期，不提供 LLM 推理循环、Agent 编排、RAG、计费、数据库、租户系统或操作系统沙箱。文件/命令执行由宿主回调或明确启用的实现提供。会话恢复接口不等于 SDK 自动持久化模型上下文。

### 版本与框架

| 维度 | 首版决策 |
|---|---|
| 协议 | 生产入口只协商 `protocolVersion: 1` |
| schema | 固定协议仓库稳定 v1 schema + meta，SHA/哈希见 sources.json |
| .NET | 首版 `net8.0`，符合当前仓库最高 TFM 约束；可被兼容的更高 .NET 宿主消费 |
| 其他目标 | netstandard 多目标需有消费者需求及 API 可用性验证后追加 |
| 平台 | Windows、Linux、macOS；不承诺浏览器/WASM 和 Unity |
| SDK 发布 | `0.x` 迭代，满足稳定覆盖与验收后 `1.0`；协议版本不随 SDK 主版本机械变化 |
| v2 | 后续显式实验命名空间与独立状态机；当前只实现 v1，不新增拆分包 |

## 2. 范围分级

| 范围 | 首轮可用版本 | 稳定 1.0 |
|---|---|---|
| 双侧 initialize/new/prompt/update/cancel、文本及资源链接、权限 | 必须 | 必须 |
| stdio/已有 Stream、JSON-RPC、强类型模型、错误及资源释放 | 必须 | 必须 |
| 稳定 v1 其余方法和 14 种 update 的模型/发送/分派支持 | 可分阶段补齐 | 必须；实际业务行为按 handler 和能力声明 |
| 文件、终端、elicitation、认证交互 | 提供必要接口，默认关闭可选能力 | 完整回调协议与确定性示例；执行策略归宿主 |
| 外部 Agent 子进程启动 | 同包 Processes 模块，调用方可选择不用 | 必须有可用实现和集成测试 |
| Native AOT | 从模型层开始验证 | 通过约定平台 smoke 后才能声称支持 |
| HTTP/WebSocket、v2、Proxy、Registry 自动安装、MCP-over-ACP | 不包含 | 独立后续范围，不作为 1.0 门槛 |

模型存在、方法可发送、业务 handler 实现，是三个不同层次；不能因已生成类型就自动宣传该能力可用。

### 功能模块与交付责任

下表为产品功能目录；具体方法、字段和能力条件只在 PROTOCOL.md 维护。“稳定版”表示拟定 1.0 的交付要求，不表示当前已完成。

| 功能域 | SDK 应提供 | 宿主应提供 | 稳定版内部模块 |
|---|---|---|---|
| 类型与序列化 | 固定 schema 生成 DTO、判别联合、方法描述、STJ context、校验与扩展字段 | 私有扩展的类型元数据 | Protocol |
| 通信 | UTF-8 NDJSON、已有 Stream、双向 RPC、ID 关联、错误和断线收敛 | 输入输出流或传输实现 | Rpc / Transport |
| 握手与认证 | initialize、协商结果、authenticate/logout 的类型化调用与分派 | 认证方法、登录交互与凭据管理 | Client / Agent |
| 会话管理 | new/load/resume/list/close/delete、分页、早到更新绑定与生命周期 | 上下文保存、恢复与历史存储 | Client / Agent |
| Prompt 与取消 | 流式 session 更新、轮次完成、会话取消及通用请求取消 | 推理、工具调度和实际停止执行 | Client / Agent / Rpc |
| 模式与配置 | modes/configOptions、设置和更新通知、能力校验 | 可用选项、配置生效与模型选择 | Client / Agent |
| 内容与更新 | 全部稳定内容类型及 14 类 update 的类型、路由和原始保留 | 渲染、工具状态展示、用量统计及计费 | Protocol / Client / Agent |
| 权限交互 | 反向请求、有效选项校验、一次性响应、取消默认值 | 审批 UI 和实际授权规则 | Client / Agent |
| 文件交互 | 读写请求/回调、参数校验与示例 | 授权根校验、文件系统访问策略和实际读写 | Client / Agent |
| 终端交互 | 五个终端方法、资源关联及清理回调契约 | 启动命令、输出缓冲、终止/回收和隔离策略 | Client / Agent |
| Elicitation | form/url 请求、结果与完成通知关联 | 表单校验、用户交互、地址展示和显式打开 | Client / Agent |
| 本地 Agent 进程 | 启动参数、stdio transport、stderr、退出和所有权管理 | 选择/安装 Agent、环境和测试身份 | Processes |
| 扩展与诊断 | 显式泛型方法注册、原始消息观察、脱敏诊断钩子 | 扩展语义、日志后端及保留策略 | Protocol / Rpc |

文件/终端示例可以实现真实操作，但不能在引用 SDK 时自动启用。内存会话示例只证明接口与生命周期；不承诺业务恢复或长期持久化。UI 状态累积器、连接自动重试、Agent 安装器和远程传输不属于首版隐式功能。

## 3. 单包与工程约束

唯一 SDK 工程、程序集和包名均为 `OpenAgent.Runtime.Acp`。Protocol、Transport、Rpc、Client、Agent、Processes、Diagnostics 是该项目内的文件夹/命名空间，不再建立对应子项目或子包。生成器、自动化测试、测试控制台不作为 SDK 包发布。

- SDK：`Backend/src/OpenAgent.Runtime.Acp/OpenAgent.Runtime.Acp.csproj`。
- 自动化测试：`Backend/tests/OpenAgent.Runtime.Acp.Tests/OpenAgent.Runtime.Acp.Tests.csproj`。
- 测试控制台：`Backend/tools/OpenAgent.Runtime.Acp.TestConsole/OpenAgent.Runtime.Acp.TestConsole.csproj`。
- 三个工程加入 `Backend/OpenAgent.sln`；复用现有集中包版本、TFM 与测试依赖管理。SDK 不引用任何平台模块、测试工程或控制台。
- 每个新增功能必须在同一步同步完成 SDK、自动化用例、控制台场景与对应文档；只生成模型、只写测试或只完成控制台都不能记为该功能完成。

内部模块、消息处理链和资源拥有权由 DESIGN.md 定义，物理目录由 DIRECTORY.md 定义。无需为了逻辑分层拆程序集，也不新增 Hosting/Testing 发布包。必要注册扩展放同包 Extensions，测试辅助留测试工程。

## 4. 两层公共 API

### 4.1 协议层

提供 `AcpClientConnection` 和 `AcpAgentConnection`。ClientConnection 向 Agent 发调用、接 Agent 的回调；AgentConnection 相反，避免用容易混淆的 Server/Client 方向命名。

每个稳定方法提供异步强类型入口，参数和结果直接对应固定 schema；低层 `PromptAsync` 返回协议 `PromptResponse`。通知同样用 Task 表示“写入完成”，不表示远端完成操作。

扩展入口接受显式 `AcpMethod<TRequest,TResponse>` 和 `JsonTypeInfo<T>`；不得要求反射扫描公共方法。原始 JSON 接口保留为逃生口，调用方承担扩展兼容。

连接创建入口拟定如下（API 形状示意，不是可编译定义）：

```text
Task<AcpClientConnection> AcpClientConnection.ConnectAsync(
    IAcpTransport transport, IAcpClientHandler handler,
    AcpConnectionOptions options, CancellationToken cancellationToken = default);

Task AcpAgentHost.RunAsync(
    IAcpTransport transport, IAcpAgentHandler handler,
    AcpConnectionOptions options, CancellationToken cancellationToken = default);
```

Client Connect 仅建立并开始 RPC 读写，不暗中初始化；调用者显式 Initialize 后创建 session。Agent Run 先注册全部接收 handler，再开始读取，持续到 EOF/关闭/故障。`IAcpTransport` 传递完整 JSON-RPC envelope，支持异步读取、串行写入、Completion 与 IAsyncDisposable；bytes/framing 由具体 transport 处理。已有 Stream 通过 `AcpStreamTransport` 包装；同包 Processes 模块只生成 stdio transport 和进程租约，不代替握手。

Client/Agent 均需 connection 级 notification 观察入口；会话便捷层由内部路由器分发事件，用户 handler 与 session 流不能竞争读取同一个队列。需要广播时显式建立各自有界订阅，不采用两个消费者随机分走事件的实现。

### 4.2 会话便捷层

```csharp
public interface IAcpClient : IAsyncDisposable
{
    Task<InitializeResponse> InitializeAsync(
        InitializeRequest request, CancellationToken cancellationToken = default);
    Task<IAcpSession> NewSessionAsync(
        NewSessionRequest request, CancellationToken cancellationToken = default);
}

public interface IAcpSession : IAsyncDisposable
{
    string SessionId { get; }
    IAsyncEnumerable<SessionNotification> ReadUpdatesAsync(
        CancellationToken cancellationToken = default);
    Task<IAcpPromptTurn> StartPromptAsync(
        PromptRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

public interface IAcpPromptTurn : IAsyncDisposable
{
    Task<PromptResponse> Completion { get; }
    Task CancelAndWaitAsync(CancellationToken cancellationToken = default);
}
```

这是主干签名提案，不是完整可编译契约；stable 方法全集按 PROTOCOL.md 补齐。session 绑定后仍接收 request 的 API 必须验证其中的 `sessionId` 与对象一致；最终可提供省略重复 ID 的便捷重载。

明确使用约束：

1. **更新属于 session。** `ReadUpdatesAsync` 是单消费者、有界流，应在 prompt 前启动消费。load 的回放、空闲时的更新也走同一通道；不提供会吞掉 session 事件的独立 turn 读取队列。
2. `Completion` 由 v1 的 prompt 响应决定，不依赖消费者是否继续枚举。消费速度超限则显式失败，不能悄悄丢片段。
3. prompt 最终响应到达前的更新先进入同 session 的有序队列；`Completion` 完成仅表示已入队，不表示 UI 已处理完。便捷层可额外提供 drain/barrier 等待。
4. 本 SDK 的便捷层默认一个 session 同时只有一轮 prompt；第二次调用立即抛本地 `SessionBusyException`，不在后台排无限队列。不同 session 可以并发。这是 SDK 策略，不宣称协议强制。
5. `StartPromptAsync` 的 token 在写入前只取消本地调用；发送后触发该 turn 的协议取消流程。仅取消一次枚举不会暗中停止远端 Agent。
6. turn Dispose 会对未结束轮次执行有界取消；session Dispose 在对端声明 close 时调用 close，否则只释放本地关联，不能伪称远端资源已关闭。清理失败通过诊断报告。
7. connection/process 的所有权显式配置 `LeaveOpen`、`OwnsProcess`；关闭一个 session 不应杀死共享 Agent 进程。

### 4.3 Handler 接口

| 接口 | 宿主提供的行为 |
|---|---|
| `IAcpAgentHandler` | 必需的初始化、新建会话、prompt 和会话取消 |
| Agent 可选 handler | 认证、load/resume/list/close/delete、模式和配置，按能力分别注册 |
| `IAcpClientHandler` | 权限选择、session 更新；更新先入内部路由，不直接执行慢 UI |
| `IAcpElicitationHandler` | form/url 交互及完成通知，按模式注册 |
| `IAcpFileSystemHandler` | 文件读写，受 session 和工作区策略约束 |
| `IAcpTerminalHandler` | 五个终端方法及 session 资源清理 |
| `IAcpAgentContext` | 给 Client 发 update、请求权限/文件/终端/elicitation，暴露协商后的能力 |

可选 handler 使用独立接口注册；可另提供便捷基类，但不以“注册一个巨大接口”自动开启全部能力。新增稳定可选能力不能迫使所有用户重新实现核心接口。构建时验证能力与 handler 一致：未配置终端不能声明 `terminal:true`。无权限 handler 时默认返回 `cancelled`，绝不默认批准。

第 4.1 节是最小创建入口；完整配置通过 Client/Agent builder 显式注册可选 handler，构建后冻结注册表，再启动读取。连接 options 只存连接参数，不混入宿主业务对象。无需 DI 容器也必须可以完成全部注册，后续同包 Extensions 仅提供可选注册适配，不依赖 ASP.NET Core。

## 5. 连接与会话状态

| 对象 | 内部状态建议 | 规则 |
|---|---|---|
| Connection | Created → Connecting → Initializing → Ready → Closing → Closed；异常为 Faulted | Ready 之前不创建会话；只初始化一次；版本不兼容时退出 |
| Session | Creating/Loading/Resuming → Ready → Prompting → Cancelling → Ready；可到 Closing/Closed/Faulted | 状态是 SDK 内部管理，不增加 v1 wire 字段 |
| Permission | Pending → Selected/Cancelled | 原始 request 恰好回复一次；重复 UI 回答不得重复发包 |
| Terminal | Created → Running → Exited/Killed → Released | 绑定 connection + session；释放后 ID 无效 |

Agent host 捕获未处理异常，转换为安全的结构化错误；正常用户拒绝、模型拒答与取消有各自语义，不能统称 InternalError。

## 6. 并发与流控

采用持续读取、独立请求分派、单一串行写入的结构。读取循环只做 framing、解析和路由，不能等待一个长时间的 prompt、用户权限 UI 或 `terminal/wait_for_exit`；否则反向请求与取消都会死锁。

- 出站 pending 表以方向和 ID 关联；对端 request ID 与本方 ID 可相同，不能共用一个无方向 key。
- 同 session 的通知按线上接收顺序进入队列；跨 session 不强行串行。
- 回调允许在处理请求时调用对端；所有异步任务由连接生命周期管理，禁止遗留未观察异常。
- `session/new`、load、resume 响应前到达的更新先按 sessionId 有界缓冲，完成绑定后转交；未能绑定的更新交全局诊断/原始事件入口，不能误绑其他会话。
- cancel、close、响应路由使用独立控制路径；等待用户操作不占用读取线程。
- 队列溢出采用显式故障：终止连接并使相关任务失败，通知宿主可能存在未确认远端执行。首版不使用有损 drop-oldest，也不无限阻塞读取循环。后续可优化为 session 级隔离。

建议默认值如下，均是产品配置，不是协议限制：

| 配置 | 默认建议 | 要求 |
|---|---:|---|
| `MaxMessageBytes` | 32 MiB | 读写均检查；按编码后字节计；宿主可调 |
| `MaxBufferedBytes` | 64 MiB / connection | 所有待处理更新/早到消息合计，不只按条数估算 |
| `MaxQueuedUpdates` | 1024 / session | 与字节上限同时生效 |
| `MaxPendingRequests` | 128 / connection | 响应、取消不因普通请求配额耗尽而无法处理 |
| `MaxSessions` | 32 / connection | 拒绝超限的新建/恢复，已存在会话可正常结束 |
| `InitializeTimeout` | 30 秒 | 可覆盖；超时不表示 Agent 未执行 |
| `RequestTimeout` | 60 秒 | 普通管理调用；prompt、权限 UI、等待终端退出分别配置，不套用此值 |
| `PromptTimeout` / `InteractionTimeout` | 默认无限，支持 token | 不因正常长任务自动重发 |
| `CancelGracePeriod` | 5 秒 | 超时转本地未确认结果；不伪造 `cancelled` |
| `ProcessShutdownGracePeriod` | 5 秒 / 阶段 | 只管理拥有的进程，最终记录退出方式 |

需要用实际负载验证默认值，达到上限时返回可识别的本地异常，并记录限制名称与大小，不记录完整敏感 payload。

## 7. 取消、超时与断线

[协议] v1 `session/cancel` 是通知；完成确认来自原 `session/prompt` 的 `stopReason: cancelled`。取消后仍接收最后一批 update；待决权限必须回复 `cancelled`。[prompt 取消](https://agentclientprotocol.com/protocol/v1/prompt-turn#cancellation)。

[协议] `$/cancel_request` 是按 request ID 的通用协作取消，可被不支持的一方忽略；支持方仍需回复原请求，可能是正常结果或 `-32800`。[通用取消](https://agentclientprotocol.com/protocol/v1/cancellation)。

SDK 必须区分：

- `CancelAsync` 只保证取消通知已写入；`CancelAndWaitAsync` 等待最终结果或取消确认，允许自然完成先赢得竞态。
- prompt 用 session 取消；普通 request token 用通用取消，原始 request 的迟到结果安全排空，不能复用 ID。
- 高层取消宽限耗尽后，当前操作报告 `AcpCancellationUnconfirmedException`，关闭该连接来回收请求表，其他共享会话随之故障；拥有的进程按策略清理。不能继续在未知状态会话上发新 prompt。
- 断线时所有 pending、更新读取和 completion 都应结束，携带 transport/process 信息；禁止把 EOF 当成正常 end_turn。
- 不自动重试 prompt、新建会话、文件写入或终端命令。协议没有给这些操作统一的 exactly-once 或幂等保证。
- 重连是新的连接和重新 initialize；随后由调用方选择已协商支持的 load/resume。SDK 不承诺自动恢复远端内存状态。

## 8. 进程、能力与执行边界

Processes 模块采用可执行文件加独立参数列表启动，默认不经 shell 拼接；`cwd`、环境覆盖、stderr 接收、启动失败与退出码显式暴露。命令和环境由可信宿主配置，不能从 Agent 提示词直接启动任意宿主程序。

`EnvironmentMode` 提供显式 Inherit / AllowList 选项，默认 Inherit 以保持 CLI 可运行；多租户宿主必须主动选择受控环境和独立凭据。诊断默认不输出环境值、认证 header 和 MCP secret。

文件 handler 按 session 的实际授权根集合校验真实路径、符号链接与跨盘/UNC 情况；不只做字符串 StartsWith。终端执行同样需要宿主策略，不能因为 ACP 能力协商成功就自动授予系统权限。SDK 不声称此类检查等同于沙箱。

AgentContext 接收到 `mcpServers` 时只提供类型和传递通道。具体 Agent handler 负责使用其 MCP 客户端连接服务器；注册了协议 handler 不表示实现了工具执行。对新 Agent 的 stdio MCP 要求见 PROTOCOL.md。

URL elicitation 由宿主展示完整地址并征得用户同意后打开；SDK 不预取、不自动访问、不收集凭据。表单不得收集跨服务密码/密钥。[elicitation](https://agentclientprotocol.com/protocol/v1/elicitation)。

## 9. 序列化、错误与可观测性

Protocol 模块提供 source-generated `JsonSerializerContext`；schema 生成代码与手写 runtime 分离。`_meta` 使用 `JsonElement` 值；不会暴露已经 Dispose 的 JsonDocument 内存。扩展字典生命周期明确。

请求/响应字段、错误码和 update 分支按 PROTOCOL.md 处理。异常分为远端 `AcpRpcException`、校验/能力错误、transport/process、超时、未确认取消及连接关闭；远端错误保留 code/message/data，默认日志隐藏敏感 data。

核心通过 BCL 的 ActivitySource/Meter 和显式诊断回调暴露事件；必要时在同包 Extensions 适配 ILogger，不强绑定 exporter。默认只记录方法、耗时、方向、结果和字节量；会话 ID 需要可配置脱敏，不作为高基数 metric 标签。payload/transcript 必须显式启用、脱敏并由宿主决定保留期限。

## 10. v2 演进隔离

v2 草案改变了 prompt 完成语义、会话恢复、能力形状、消息补丁，并移除 Client 文件/终端执行 API。增加 `protocolVersion=2` 不能复用 v1 全部状态机。[官方迁移](https://agentclientprotocol.com/protocol/v2/migration)。

未来如实施 v2，在同一 SDK 的 `Experimental.V2` 命名空间维护独立 DTO、handler、session 状态机和测试；共用 transport/RPC 基础设施，不因此新增发布包。对外统一模型只能放显式映射层，不把 v2 state_update 强行伪装为 v1 prompt 响应。

## 11. Agent.Matrix 消费规格

本 SDK 在指定目录内作为一个项目开发，可通过项目引用或同名单包供消费者使用；SDK 不引用其他 OpenAgent 平台模块。Contracts 仍保持纯平台契约，平台的 ACP 转换放在拥有该运行时接入的模块；新增模块/项目依赖需先符合既有架构约束。

平台适配器负责 tenant/user 身份、conversationId ↔ sessionId 归属、文件授权、审批、审计、工作区与 Runner。ACP sessionId 不能当作授权凭证。平台 HTTP/SSE 与 ACP stdio 分别维护自己的契约，不让 SDK 默认在 Router 或公共 API 宿主进程启动外部 CLI。

平台集成另行定义映射与依赖方向，不在纯 SDK 中耦合租户、数据库或推理框架。新建工程不表示已改造平台执行链；不得通过扩大现有依赖白名单让 Core 引用实现细节。
