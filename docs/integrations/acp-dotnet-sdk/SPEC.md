# ACP 单包 SDK 规格

## 范围与工程

唯一 SDK 工程/程序集/包名为 `OpenAgent.Runtime.Acp`，位于 `Backend/src/OpenAgent.Runtime.Acp`，目标 `net10.0`，复用 `dev` 的集中配置。Protocol、Transport、Rpc、Client、Agent、Processes、Diagnostics、Extensions 是同包内部模块；测试、控制台、生成器不可发布为 SDK 包。SDK 不引用其他平台项目。

稳定入口只协商 ACP v1，协议来源固定于 [sources.json](./sources.json)。完整目标为双侧协议、会话、流式更新、取消、进程及可选交互；当前实现状态只记 [TODO](../../planning/TODO.md)。类型已生成、方法可发送、业务 handler 可执行必须分别验收。

SDK 负责通信和生命周期，推理、持久化、审批 UI、文件授权、命令执行、租户系统与沙箱归宿主。HTTP/WS、v2、代理编排、自动安装和平台执行链集成不在本轮范围。支持矩阵以实测为准，不承诺浏览器/WASM/Unity；0.x 迭代，满足 [发布门槛](./VALIDATION.md) 后才具备 1.0 候选资格。

## 公共 API 契约（除 Transport 外均为待实现设计）

- 协议层 `AcpClientConnection` / `AcpAgentConnection` 按 [PROTOCOL](./PROTOCOL.md) 提供强类型异步请求/通知，低层 Prompt 返回协议响应。泛型扩展显式注册 `AcpMethod<TRequest,TResponse>` 与 `JsonTypeInfo<T>`；原始 JSON 为显式逃生口，不反射扫描 handler，不泄漏第三方 RPC 类型。
- Client Connect 只启动 RPC，不暗中 initialize。Agent Host 在读循环前冻结全部 handler 注册，Run 持续到 EOF/关闭/故障。无需 DI 容器；options 只放连接配置，宿主行为通过 builder/handler 注册。
- `IAcpTransport` 传递不含分隔符的完整 UTF-8 消息体，提供 ReadAsync、WriteAsync、Completion 和 IAsyncDisposable；只做 framing。已有 Stream 用 AcpStreamTransport，进程模块提供 transport 与 ProcessLease，不代替握手。
- Transport 的消息上限不含 LF/CRLF；跨 read 最多暂存一个候选 CR，不放宽消息体上限。输入 EOF 进入终态，拒绝新操作/尚未开始的排队写，并等待已经开始的完整帧写入；不支持继续发起半关闭写入。Dispose/IO 故障可取消此排空过程。
- Completion 在所有在途操作及拥有资源清理结束后才完成，保留 IO/清理故障。并发或重复 Dispose 等待同一清理结果；取消回调异常仍尝试关闭全部拥有的流，合并多个清理异常。已报告的 IO 故障由 Completion 保留，Dispose 仅重抛清理故障；LeaveOpen 流不被关闭。自定义 Stream 必须使 IO 在取消或释放时收敛。
- 必需 Agent handler 处理 initialize/new/prompt/cancel；认证、恢复、列表、关闭、删除、配置等按独立可选接口注册。Client handler 提供权限、更新及可选文件/终端/elicitation 回调；AgentContext 向 Client 发送更新和反向请求。
- 能力由已注册 handler 与显式启用项共同生成并校验；接口存在不表示启用。无权限 handler 返回 cancelled，不默认批准。新增可选能力不能强迫现有宿主实现巨大接口。

会话便捷层：Client 提供 InitializeAsync/NewSessionAsync 等入口；Session 提供 SessionId、ReadUpdatesAsync、StartPromptAsync、CancelAsync；PromptTurn 提供 Completion、CancelAndWaitAsync。所有对象支持异步释放。绑定 session 后仍接受带 sessionId 的请求时必须校验一致性。

## 会话、顺序与状态

- 连接只初始化一次，Ready 前不能创建会话，版本不兼容即退出。Session 由创建/加载/恢复进入 Ready，经 Prompting/Cancelling 回 Ready，也可关闭或故障；内部状态不增加 v1 wire 字段。
- 更新属于 session，ReadUpdatesAsync 为单消费者有界流，在 prompt 前开始消费；load 回放及空闲更新同样进入此流。连接通知观察者与 Session 流不得竞争消费，需要广播时建立各自有界订阅。
- 默认同 session 只允许一轮 prompt，第二轮本地 SessionBusyException 立即失败；不同 session 可并发。该限制是 SDK 策略。
- 最终响应前的更新先按线上顺序入队，再完成 PromptTurn；Completion 表示已入队，不表示 UI 处理完，也不依赖消费者继续枚举。
- new/load/resume 响应前的更新按 sessionId 有界缓存，绑定后转交；无法绑定的事件可观测，不误投其他 session。慢消费或条数/字节溢出显式使连接及相关任务故障，不丢片段或无限阻塞 reader。
- Session Dispose 在协商支持时请求远端 close，否则只释放本地关联；Turn Dispose 对未结束轮次执行有界取消。两者都不杀共享进程；持久化历史不由 SDK 自动删除。

## 并发、配额与取消

持续 reader 只做读取/校验/路由，请求独立分派，writer 串行完整帧。reader 不等待 prompt、权限 UI 或 terminal wait；回调可重入调用对端。入/出站 ID 表分离，普通请求配额耗尽不能阻塞响应、取消和关闭；所有任务由连接生命周期观察并收敛。

| 建议默认配置（产品策略，非协议限制） | 值 |
|---|---:|
| MaxMessageBytes（按 UTF-8 字节，读写均检查） | 32 MiB |
| MaxBufferedBytes（连接所有更新与早到消息合计） | 64 MiB |
| MaxQueuedUpdates / session | 1024 |
| MaxPendingRequests / connection | 128 |
| MaxSessions / connection | 32 |
| InitializeTimeout / 普通 RequestTimeout | 30s / 60s |
| PromptTimeout / InteractionTimeout | 默认无限，支持 token |
| CancelGracePeriod / ProcessShutdownGracePeriod | 各 5s，进程按阶段执行 |

默认值需负载验证；超限异常记录限制名称和大小，不记录敏感正文。

- 写前 token 取消只取消本地调用；prompt 发送后使用 session/cancel，普通 request 使用通用请求取消。取消一次更新枚举不暗中停止远端。
- CancelAsync 仅保证通知已写入；CancelAndWaitAsync 等待原 prompt 响应，允许自然完成赢得竞态，继续接收取消尾部更新。待决权限回复 cancelled。
- 取消宽限耗尽抛 AcpCancellationUnconfirmedException 并关闭连接，回收请求表，其他共享 session 随之故障；不能报告假 cancelled 或继续未知状态会话。
- EOF/故障结束全部 pending、更新流和 Completion，保留 transport/process 信息，不能伪装 end_turn。迟到结果安全排空且不复用 ID；不自动重试 prompt/new/文件写/命令。重连须重新 initialize，再由调用方选择协商过的 load/resume。

## 执行、安全与诊断

Processes 使用 executable + ArgumentList，默认不拼 shell；显式提供 cwd、环境、stderr、启动失败及退出码。EnvironmentMode 支持 Inherit（默认）/AllowList；多租户宿主主动选择受控环境及专用身份。只回收自己拥有的进程，持续排空 stderr。

文件 handler 按 session 授权根校验真实路径、符号链接、跨盘/UNC，不只做字符串 StartsWith；终端由宿主策略授权，能力协商不授予系统权限，也不等同沙箱。MCP DTO 只传递配置，实际 MCP 客户端/工具执行由 Agent handler 实现。

URL elicitation 由宿主展示完整地址并在用户同意后打开；SDK 不预取、不自动访问或收集凭证，表单不收集跨服务密码/密钥。默认测试仅用临时目录、自有进程、确定性身份；模型、真实账户、URL 打开和花费需额外显式启用，默认不 logout、不访问个人仓库。

模型使用 source-generated STJ context；_meta/开放 JSON 的内存拥有权独立于已释放 JsonDocument。区分远端 AcpRpcException（保留 code/message/data）与本地校验、能力、IO、超时、未确认取消和关闭异常；Agent 未处理异常转换为安全错误，正常拒绝/取消不统称 InternalError。

通过 ActivitySource/Meter 和显式诊断回调记录方法、方向、耗时、结果及字节量；不绑定 exporter。默认不记录凭据、环境值、正文；session ID 可脱敏且不用作高基数 metric 标签。transcript 显式开启、脱敏并由宿主控制保留期。

平台适配后续另定：身份、conversationId/sessionId 映射、授权、审计、存储和 HTTP/SSE 属平台。sessionId 不是授权凭证；SDK 不默认在 Router/Host 启动 CLI，不扩大平台依赖白名单。未来 v2 必须独立实验命名空间/状态机，不能只改版本号或伪装为 v1 prompt 响应。
